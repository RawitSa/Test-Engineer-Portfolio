using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace camera_show {
    /// <summary>
    /// Runs ONE inspection job (Check LED, Check OCR, Read2D or Blink LED) on ONE camera, from open to close.
    /// Everything it touches is instance state (its own VideoCapture, ROI, Tesseract engine,
    /// stopwatches), so several workers can run at the same time without sharing anything
    /// except the camera-open lock and the result-file names (one file per head).
    /// </summary>
    public sealed partial class CameraWorker {
        // DirectShow can fail or hand out the wrong device when several VideoCaptures are created
        // at the same instant, so only the *open + configure* phase is serialised. Grabbing and
        // processing frames runs fully in parallel.
        internal static readonly SemaphoreSlim OpenLock = new SemaphoreSlim(1, 1);
        internal static readonly VideoCapture.API CaptureApi = DetectCaptureApi();

        /// <summary>Text written after the OCR text on success. Kept as the original program wrote it.</summary>
        private const string OcrPassToken = "True";
        private const int PreviewIntervalMs = 100;
        private const int PreviewMaxWidth   = 800;
        private const int RemotePreviewIntervalMs = 1500;

        private readonly JobSpec _job;
        private readonly CancellationToken _cancel;
        private readonly CameraSkipSettings _skip = new CameraSkipSettings();
        private readonly Stopwatch _previewClock = Stopwatch.StartNew();

        // Black-frame recovery: same thresholds as Form1 (BlackFrameThreshold / BlackFrameMeanThreshold / ReopenCooldownMs).
        private const int    BlackFrameLimit  = 15;
        private const double BlackMeanSum     = 8.0;
        private const int    ReopenCooldownMs = 3000;
        private VideoCapture _cap;                       // current capture; replaced when the camera is reopened
        private bool         _testMode;                  // live test run on the tile (see RunTest): camera belongs to LivePreview
        private Action       _pump;                      // lets LivePreview run its queued commands between two frames
        private DateTime     _lastReopen = DateTime.MinValue;
        private long         _frames, _blackFrames;
        private int          _blackRun, _reopens;
        private double       _lastMean;

        // ── Per-job settings (loaded from the same CSV/txt files as the single-camera app) ──
        private StepConfig _cfg;
        private int       _port;
        private int       _digitSn, _adjustDeg, _roiMove, _roiStartMs;
        private bool      _roiSweepOn;
        private int       _timeOutMs;
        private Rectangle _rect;
        private bool      _hsvFlag;
        private int       _hsvMask;
        private int       _hsvTimeout;
        private Hsv       _hsvLow, _hsvHigh;
        private Bgr       _bgrLow, _bgrHigh;

        /// <summary>Raised ~10x/s with a COPY of the annotated frame. The receiver must Dispose it.</summary>
        public event Action<int, Bitmap> FrameReady;
        /// <summary>Raised on state changes ("Opening camera", "Step 1/2", "PASS", ...).</summary>
        public event Action<int, string> StatusChanged;

        public CameraWorker(JobSpec job, CancellationToken cancel) {
            _job = job;
            _cancel = cancel;
        }

        // ── Entry point ──────────────────────────────────────────────────────

        /// <summary>Blocking. Opens the camera, runs the check, writes the result file, releases the camera.</summary>
        public JobResult Run() {
            JobResult result;
            VideoCapture cap = null;
            try {
                LoadSettings();
                Status("Opening camera");
                cap = _cap = OpenCamera();
                CameraHub.Log(_job + " opened port " + _port + ", camera reports " + cap.Width + "x" + cap.Height);
                Status("Checking");
                result = RunKind(cap);
            } catch (Exception ex) {
                result = Fail("Error: " + ex.Message);
            } finally {
                try { if (_cap != null) _cap.Dispose(); } catch { }
            }

            CameraHub.Log(_job + " frames=" + _frames + " black=" + _blackFrames + " reopens=" + _reopens +
                          " lastBrightnessSum=" + _lastMean.ToString("F1"));
            WriteResult(result);
            Status(result.Pass ? "PASS" : "FAIL: " + result.Message);
            return result;
        }

        private JobResult RunKind(VideoCapture cap) {
            switch (_job.Kind) {
                case JobKind.CheckLed: return RunLed(cap);
                case JobKind.CheckOcr: return RunOcr(cap);
                case JobKind.Read2d:   return RunRead2d(cap);
                case JobKind.Compare:  return RunCompare(cap);
                case JobKind.MultiLed: return RunMultiLed(cap);
                default:               return RunBlink(cap);
            }
        }

        /// <summary>
        /// "Start live view" on a tile: runs this mode's check on a camera that is ALREADY open (owned by LivePreview), exactly like
        /// a normal job - same settings, same overlays, same decisions - but over and over: no time-out, no result file, the camera
        /// stays open. A PASS is shown for about a second, then the check starts again. Returns when cancelled.
        /// Throws when the mode cannot run (no template, missing settings ...); the caller then shows the plain picture.
        /// <paramref name="pump"/> is called between frames so LivePreview can still serve its commands (ROI crop, camera values ...).
        /// </summary>
        internal void RunTest(VideoCapture cap, Action pump) {
            _testMode = true; _pump = pump; _cap = cap;
            LoadSettings();
            _timeOutMs = int.MaxValue;                              // a live check never gives up
            while (!_cancel.IsCancellationRequested) {
                Status("Checking");
                JobResult r = RunKind(cap);
                if (_cancel.IsCancellationRequested) break;
                if (!r.Pass) throw new InvalidOperationException(r.Message);
                Status("PASS");
                for (int i = 0; i < 12 && !_cancel.IsCancellationRequested; i++) {      // keep the PASS picture on screen
                    if (_pump != null) _pump();
                    Thread.Sleep(100);
                }
            }
        }

        // ── Settings ─────────────────────────────────────────────────────────

        private void LoadSettings() {
            StepConfig cfg = _cfg = StepConfig.Load(_job.ModeName);
            int h = _job.Head;

            _port = _job.PortOverride ?? cfg.GetInt(h, HeadConfigKey.Port);

            // Read2D settings (defaults match the single-camera app when a key is missing)
            _digitSn     = cfg.GetIntOr (h, HeadConfigKey.DigitSN,       13);
            _adjustDeg   = cfg.GetIntOr (h, HeadConfigKey.AdjustDegree,  0);
            _roiSweepOn  = cfg.GetBoolOr(h, HeadConfigKey.RoiMoveSet,    false);
            _roiMove     = cfg.GetIntOr (h, HeadConfigKey.RoiMove,       10);
            _roiStartMs  = cfg.GetIntOr (h, HeadConfigKey.StartTimeOut,  500);

            _rect = new Rectangle(
                cfg.GetInt(h, HeadConfigKey.RectX),
                cfg.GetInt(h, HeadConfigKey.RectY),
                cfg.GetInt(h, HeadConfigKey.RectWidth),
                cfg.GetInt(h, HeadConfigKey.RectHeight));

            _hsvFlag    = cfg.GetBool(h, HeadConfigKey.HsvFlag);
            _hsvMask    = cfg.GetInt (h, HeadConfigKey.HsvMask);
            _hsvTimeout = cfg.GetInt (h, HeadConfigKey.HsvTimeOut);

            string[] p = cfg.Get(h, HeadConfigKey.HsvFormat).Split(' ');
            var sv = new int[6];
            for (int i = 0; i < 6; i++) sv[i] = Convert.ToInt32(p[i]);
            if (_hsvFlag) {
                _hsvLow  = new Hsv(sv[0], sv[2], sv[4]);
                _hsvHigh = new Hsv(sv[1], sv[3], sv[5]);
            } else {
                _bgrLow  = new Bgr(sv[0], sv[2], sv[4]);
                _bgrHigh = new Bgr(sv[1], sv[3], sv[5]);
            }

            // Overall timeout: per-head file wins, otherwise the value on the job.
            _timeOutMs = _job.TimeOutMs;
            try {
                _timeOutMs = Convert.ToInt32(File.ReadAllText(
                    AppFilePath.Folder + h + AppFilePath.TimeOut).Trim());
            } catch { }

            _skip.Load(AppFilePath.Folder + h);
        }

        // ── Camera open / configure ──────────────────────────────────────────

        private VideoCapture OpenCamera() =>
            OpenCapture(_cfg, _job.Head, _port, _skip, _cancel);

        /// <summary>Opens + configures a camera exactly like a job does. Also used by the live setup preview.</summary>
        internal static VideoCapture OpenCapture(StepConfig cfg, int h, int port, CameraSkipSettings skip, CancellationToken cancel) {
            VideoCapture cap = null;

            OpenLock.Wait(cancel);
            try {
                for (int attempt = 0; attempt < 3; attempt++) {
                    if (cap != null) { try { cap.Dispose(); } catch { } cap = null; }
                    try { cap = new VideoCapture(port, CaptureApi); } catch { }
                    if (cap != null && cap.Ptr != IntPtr.Zero && cap.Width > 0) break;
                    Thread.Sleep(300);
                }
                if (cap == null || cap.Ptr == IntPtr.Zero || cap.Width <= 0) {
                    if (cap != null) { try { cap.Dispose(); } catch { } }
                    throw new InvalidOperationException($"Can not open camera port {port} \r\nFAIL");
                }

                ApplyFourcc(cap, h);           // optional MJPG: far less USB bandwidth than the uncompressed default
                ApplyProp(skip, cap, CapProp.FrameHeight, cfg.GetInt(h, HeadConfigKey.FrameHeight));
                ApplyProp(skip, cap, CapProp.FrameWidth,  cfg.GetInt(h, HeadConfigKey.FrameWidth));
                // Many cameras connected -> a smaller capture (USB bandwidth); fewer cameras -> back to the CSV size.
                Size lower = AutoResolution.Choose(h, new Size((int)cap.Width, (int)cap.Height));
                if (!lower.IsEmpty) {
                    ApplyProp(skip, cap, CapProp.FrameHeight, lower.Height);
                    ApplyProp(skip, cap, CapProp.FrameWidth,  lower.Width);
                }
                ApplyProp(skip, cap, CapProp.Zoom,        cfg.GetInt(h, HeadConfigKey.Zoom));
                ApplyProp(skip, cap, CapProp.Pan,         cfg.GetInt(h, HeadConfigKey.Pan));
                ApplyProp(skip, cap, CapProp.Tilt,        cfg.GetInt(h, HeadConfigKey.Tilt));
                ApplyProp(skip, cap, CapProp.Contrast,    cfg.GetInt(h, HeadConfigKey.Contrast));
                ApplyProp(skip, cap, CapProp.Brightness,  cfg.GetInt(h, HeadConfigKey.Brightness));
                ApplyProp(skip, cap, CapProp.Saturation,  cfg.GetInt(h, HeadConfigKey.Saturation));
                ApplyProp(skip, cap, CapProp.Sharpness,   cfg.GetInt(h, HeadConfigKey.Sharpness));
                ApplyProp(skip, cap, CapProp.Gain,        cfg.GetInt(h, HeadConfigKey.Gain));
                Thread.Sleep(200); // let the DirectShow pipeline settle
            } catch {
                if (cap != null) { try { cap.Dispose(); } catch { } }
                throw;
            } finally {
                OpenLock.Release();
            }

            // Drain stale frames OUTSIDE the lock so the next camera can start opening meanwhile.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 500 && !cancel.IsCancellationRequested) {
                try { using (cap.QueryFrame()) { } } catch { }
            }
            return cap;
        }

        /// <summary>
        /// Reads one frame and watches for black ones. After BlackFrameLimit in a row the camera is
        /// reopened (with a cooldown), exactly like Form1.HandleBlackFrame. The frame is still returned.
        /// </summary>
        private Mat Grab() {
            if (_pump != null) _pump();
            Mat f = null;
            try { f = _cap.QueryFrame(); } catch { }
            _frames++;

            bool black = true;
            if (f != null && !f.IsEmpty) {
                MCvScalar m = CvInvoke.Mean(f);
                _lastMean = m.V0 + m.V1 + m.V2;
                black = _lastMean < BlackMeanSum;
            }

            if (!black) { _blackRun = 0; return AutoResolution.Normalize(_job.Head, f); }
            _blackFrames++;
            if (++_blackRun >= BlackFrameLimit) { _blackRun = 0; ReopenCamera(); }
            return AutoResolution.Normalize(_job.Head, f);
        }

        private void ReopenCamera() {
            if (_cancel.IsCancellationRequested) return;
            if (_testMode) return;                                  // the camera belongs to LivePreview, which keeps it
            if ((DateTime.UtcNow - _lastReopen).TotalMilliseconds < ReopenCooldownMs) return;   // never thrash the hardware
            _lastReopen = DateTime.UtcNow;
            _reopens++;
            CameraHub.Log(_job + " black frames -> reopening camera port " + _port);
            Status("Reopening camera");
            try { if (_cap != null) _cap.Dispose(); } catch { }
            _cap = null;
            Thread.Sleep(800);
            _cap = OpenCamera();          // throws -> Run() turns it into a FAIL result
            Status("Checking");
        }

        /// <summary>
        /// config/test_head_N_fourcc.txt holding "MJPG" asks the camera for compressed video. Several cameras on one
        /// USB controller / hub often only work at full resolution with MJPG. No file = driver default.
        /// </summary>
        private static void ApplyFourcc(VideoCapture cap, int head) {
            try {
                string path = AppFilePath.Folder + head + "_fourcc.txt";
                if (!File.Exists(path)) return;
                string t = File.ReadAllText(path).Trim();
                if (t.Length != 4) return;
                cap.SetCaptureProperty(CapProp.FourCC, VideoWriter.Fourcc(t[0], t[1], t[2], t[3]));
            } catch { }
        }

        private void Apply(VideoCapture cap, CapProp prop, double value) => ApplyProp(_skip, cap, prop, value);

        /// <summary>Same rules as SetCamera.SetCapture: -1 or a skipped property is not written.</summary>
        internal static void ApplyProp(CameraSkipSettings skip, VideoCapture cap, CapProp prop, double value) {
            if (value == -1 || skip.ShouldSkip(prop)) return;
            cap.SetCaptureProperty(prop, value);
        }

        // ── Check LED ────────────────────────────────────────────────────────

        private JobResult RunLed(VideoCapture cap) {
            List<Rectangle> steps = BuildLedSteps();
            int step = 0;
            var total = Stopwatch.StartNew();
            var hold  = Stopwatch.StartNew();   // how long the colour has been continuously present
            var readjust = Stopwatch.StartNew(); // ReAdjust parity: see below
            int baseBrightness = _cfg.GetIntOr(_job.Head, HeadConfigKey.Brightness, -1);

            while (!_cancel.IsCancellationRequested) {
                if (total.ElapsedMilliseconds >= _timeOutMs)
                    return Fail("(" + (step + 1) + ")time over");

                bool stepDone = false;
                using (Mat frame = Grab()) {
                    if (frame == null || frame.IsEmpty) { Thread.Sleep(10); continue; }
                    using (Image<Bgr, byte> bgr = frame.ToImage<Bgr, byte>()) {
                        Rectangle roi = Clamp(steps[step], bgr.Size);
                        int count = CountColor(bgr, roi);

                        if (count >= _hsvMask) stepDone = hold.ElapsedMilliseconds >= _hsvTimeout;
                        else {
                            hold.Restart();
                            // The single-camera app (ReAdjust.Run, production only) re-applies
                            // baseline brightness - 5 on every missed frame. Re-assert at most once a second.
                            if (baseBrightness != -1 && readjust.ElapsedMilliseconds >= 1000) {
                                readjust.Restart();
                                //try { Apply(_cap, CapProp.Brightness, baseBrightness - 5); } catch { }
                                try { Apply(_cap, CapProp.Brightness, baseBrightness); } catch { }
                            }
                        }

                        CvInvoke.Rectangle(bgr, roi, new MCvScalar(0, 0, 255), 2);
                        CvInvoke.PutText(bgr, "Head " + _job.Head + " LED step " + (step + 1) + "/" + steps.Count,
                            new Point(20, 30), FontFace.HersheySimplex, 0.7, new MCvScalar(0, 0, 255), 2);
                        CvInvoke.PutText(bgr, "pixels=" + count + " hold=" + hold.ElapsedMilliseconds + "ms",
                            new Point(20, 60), FontFace.HersheySimplex, 0.7, new MCvScalar(0, 0, 255), 2);
                        Preview(bgr);
                    }
                }

                if (stepDone) {
                    step++;
                    if (step >= steps.Count) return Pass("Color Detected");
                    hold.Restart();
                    Status("Step " + (step + 1) + "/" + steps.Count);
                }
                Thread.Sleep(20);
            }
            return Fail("Cancelled");
        }

        /// <summary>
        /// Step 1 uses the ROI from the CSV. Further steps exist while config/check_led/{head}Image{n}.png
        /// exists, and take line n-1 of the {head}Rectangle*.txt files (same convention as Form1.CheckLedMode).
        /// </summary>
        private List<Rectangle> BuildLedSteps() {
            var steps = new List<Rectangle> { _rect };
            string dir = StepConfig.ConfigDir + _job.StepName + "/";
            string pre = dir + _job.Head;

            for (int n = 2; File.Exists(pre + "Image" + n + ".png"); n++) {
                Rectangle r = steps[steps.Count - 1];   // fall back to previous ROI if a line is bad
                try {
                    r = new Rectangle(
                        Convert.ToInt32(File.ReadAllLines(pre + AppFilePath.RectX)     [n - 1]),
                        Convert.ToInt32(File.ReadAllLines(pre + AppFilePath.RectY)     [n - 1]),
                        Convert.ToInt32(File.ReadAllLines(pre + AppFilePath.RectWidth) [n - 1]),
                        Convert.ToInt32(File.ReadAllLines(pre + AppFilePath.RectHeight)[n - 1]));
                } catch { }
                steps.Add(r);
            }
            return steps;
        }

        private int CountColor(Image<Bgr, byte> bgr, Rectangle roi) {
            if (roi.Width <= 0 || roi.Height <= 0) return 0;
            using (Image<Bgr, byte> crop = bgr.Copy(roi)) {
                if (!_hsvFlag) {
                    using (Image<Gray, byte> mask = crop.InRange(_bgrLow, _bgrHigh))
                        return mask.CountNonzero()[0];
                }
                using (Image<Hsv, byte> hsv = crop.Convert<Hsv, byte>())
                using (Image<Gray, byte> mask = hsv.InRange(_hsvLow, _hsvHigh))
                    return mask.CountNonzero()[0];
            }
        }

        // ── Check OCR ────────────────────────────────────────────────────────

        private JobResult RunOcr(VideoCapture cap) {
            if (string.IsNullOrEmpty(_job.ExpectedText))
                return Fail("No expected OCR text given");

            var total = Stopwatch.StartNew();
            // One Tesseract engine PER worker: a Tesseract instance is not thread-safe.
            using (var ocr = CreateOcrEngine()) {
                while (!_cancel.IsCancellationRequested) {
                    if (total.ElapsedMilliseconds >= _timeOutMs)
                        return Fail("(1)time over");

                    string text = string.Empty;
                    using (Mat frame = Grab()) {
                        if (frame == null || frame.IsEmpty) { Thread.Sleep(10); continue; }
                        using (Image<Bgr, byte> raw = frame.ToImage<Bgr, byte>())
                        using (Image<Bgr, byte> img = raw.Rotate(180, new Bgr(0, 0, 0))) {   // camera is mounted upside-down
                            Rectangle roi = Clamp(_rect, img.Size);
                            text = ReadText(ocr, img, roi);

                            CvInvoke.Rectangle(img, roi, new MCvScalar(0, 255, 255), 2);
                            CvInvoke.PutText(img, "Head " + _job.Head + " Read: " + text,
                                new Point(20, 30), FontFace.HersheySimplex, 0.7, new MCvScalar(0, 0, 255), 2);
                            Preview(img);
                        }
                    }

                    if (string.Equals(text, _job.ExpectedText, StringComparison.Ordinal))
                        return Pass(text);

                    Thread.Sleep(100);
                }
            }
            return Fail("Cancelled");
        }

        private static Emgu.CV.OCR.Tesseract CreateOcrEngine() {
            var e = new Emgu.CV.OCR.Tesseract(AppFilePath.TessData, "eng", Emgu.CV.OCR.OcrEngineMode.TesseractLstmCombined);
            e.SetVariable("preserve_interword_spaces", "1");
            e.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz ");
            e.PageSegMode = Emgu.CV.OCR.PageSegMode.SingleLine;
            return e;
        }

        /// <summary>Same preprocessing as Form1.ReadOcrText: gray → blur → Otsu → invert → 3x upscale → binary.</summary>
        private static string ReadText(Emgu.CV.OCR.Tesseract ocr, Image<Bgr, byte> img, Rectangle roi) {
            if (roi.Width <= 0 || roi.Height <= 0) return string.Empty;
            try {
                using (Image<Bgr, byte> crop = img.Copy(roi))
                using (Image<Gray, byte> gray = crop.Convert<Gray, byte>())
                using (var otsu = new Mat())
                using (var inv = new Mat())
                using (var big = new Mat()) {
                    CvInvoke.GaussianBlur(gray, gray, new Size(5, 5), 0);
                    CvInvoke.Threshold(gray.Mat, otsu, 0, 255, ThresholdType.Otsu | ThresholdType.Binary);
                    CvInvoke.BitwiseNot(otsu, inv);
                    CvInvoke.Resize(inv, big, new Size(inv.Cols * 3, inv.Rows * 3), 0, 0, Inter.Cubic);
                    CvInvoke.Threshold(big, big, 127, 255, ThresholdType.Binary);

                    using (Image<Gray, byte> final = big.ToImage<Gray, byte>()) {
                        ocr.SetImage(final);
                        ocr.Recognize();
                        return (ocr.GetUTF8Text() ?? string.Empty).Trim();
                    }
                }
            } catch {
                return string.Empty;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static Rectangle Clamp(Rectangle r, Size bounds) =>
            Rectangle.Intersect(r, new Rectangle(Point.Empty, bounds));

        private void Preview(Image<Bgr, byte> annotated, bool force = false) {
            // Remote mode (AnyDesk): a picture only every RemotePreviewIntervalMs instead of ~10 per second.
            int interval = CameraHub.RemoteMode ? RemotePreviewIntervalMs : PreviewIntervalMs;
            if (!force && _previewClock.ElapsedMilliseconds < interval) return;
            Action<int, Bitmap> handler = FrameReady;
            if (handler == null) return;
            _previewClock.Restart();

            // Independent copy of the part the operator zoomed into. Image<,>.Bitmap is only a VIEW over the image's own
            // pixel buffer and `annotated` is disposed as soon as we return, so a clone could be painted after its memory
            // was freed -> AccessViolationException. TileView.Make always builds a separate bitmap.
            Bitmap copy;
            try { copy = TileView.Make(_job.Head, annotated, PreviewMaxWidth); }
            catch { return; }                                         // a dropped preview frame is harmless
            handler(_job.Head, copy);
        }

        private void Status(string s) {
            Action<int, string> handler = StatusChanged;
            if (handler != null) handler(_job.Head, s);
        }

        private JobResult Pass(string msg) => new JobResult { Job = _job, Pass = true,  Message = msg };
        private JobResult Fail(string msg) => new JobResult { Job = _job, Pass = false, Message = msg };

        /// <summary>Same file name and "message\r\nPASS|FAIL" format that fMain already reads.</summary>
        public static void WriteResult(JobResult r) {
            string verdict = r.Pass ? (r.Job.Kind == JobKind.CheckOcr ? OcrPassToken : "PASS") : "FAIL";
            try { File.WriteAllText(r.Job.ResultFile, r.Message + "\r\n" + verdict); } catch { }
        }

        private static VideoCapture.API DetectCaptureApi() {
            try {
                var info = new Microsoft.VisualBasic.Devices.ComputerInfo();
                if (info.OSFullName.Contains("Windows 7")) return VideoCapture.API.Any;
            } catch { }
            return VideoCapture.API.DShow;
        }
    }
}
