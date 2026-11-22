using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Threading;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using ZXing;

namespace camera_show {
    /// <summary>
    /// A live camera view for SETUP (right-click on a tile). Owns one camera on its own STA thread.
    /// Everything that touches the VideoCapture - property changes, the driver dialog, saving a crop -
    /// is posted to that thread with <see cref="Post"/>, so the capture is only ever used by one thread.
    /// It opens the camera with the same code and the same CSV values as a real job, and shows the picture
    /// in the same orientation the job sees (OCR: rotated 180; Read2D: Adjust Degree), so a drawn ROI matches.
    /// </summary>
    public sealed class LivePreview {
        private const int FrameIntervalMs = 60;
        private const int PreviewMaxWidth = 800;

        public int    Head        { get; private set; }
        public string StepName    { get; private set; }
        public int    Port        { get; private set; }
        /// <summary>True when the port was picked by the operator: it is written to the CSV once the camera opened.</summary>
        public bool   PersistPort { get; private set; }
        public CameraSkipSettings Skip { get; } = new CameraSkipSettings();

        public event Action<int, Bitmap>               FrameReady;
        public event Action<int, string>               StatusChanged;
        public event Action<LivePreview, bool, string> Opened;      // (preview, success, error text)

        private readonly ConcurrentQueue<Action<VideoCapture>> _cmds = new ConcurrentQueue<Action<VideoCapture>>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Thread _thread;

        // What the overlay needs (written by the UI thread, read by the preview thread)
        private readonly object _vis = new object();
        private Rectangle _roi = Rectangle.Empty;
        private double _rotate;
        private bool _hsvTest, _hasColor, _hsvFlag;
        private int _hsvMask;
        private Hsv _hsvLow, _hsvHigh;
        private Bgr _bgrLow, _bgrHigh;
        private Size _frameSize;

        // Streaming = continuous video. Not streaming = "one frame at a time": the camera stays open, but a picture is
        // only sent when asked for (RequestFrame) or re-cut from the stored one (zoom / ROI changed). Much lighter over
        // a remote desktop such as AnyDesk.
        private volatile bool _streaming = true;
        private Image<Bgr, byte> _snap;                // the last captured frame (preview thread only)

        public LivePreview(int head, string stepName, int port, bool persistPort) {
            Head = head; StepName = stepName; Port = port; PersistPort = persistPort;
        }

        // ── Public control (any thread) ──────────────────────────────────────

        public void Start() {
            _thread = new Thread(Loop) { IsBackground = true, Name = "preview-head" + Head };
            _thread.SetApartmentState(ApartmentState.STA);   // DirectShow property dialogs need an STA thread
            _thread.Start();
        }

        /// <summary>Runs on the preview thread, between two frames, with the live capture.</summary>
        public void Post(Action<VideoCapture> command) { _cmds.Enqueue(command); }

        /// <summary>Releases the camera and the port. Blocks (max 3 s) until the thread has let go of the camera.</summary>
        public void Stop() {
            try { _cts.Cancel(); } catch { }
            Thread t = _thread;
            if (t != null && t != Thread.CurrentThread) t.Join(3000);
            PortResolver.Release(Head);
        }

        /// <summary>Size of the full camera frame as shown (after rotation). Needed to map mouse clicks to ROI pixels.</summary>
        public Size FrameSize { get { lock (_vis) return _frameSize; } }

        public bool Streaming { get { return _streaming; } set { _streaming = value; } }

        // ── "Start live view" = run the mode like a normal job ───────────────
        private volatile bool _runMode;
        private volatile bool _restart;
        private CancellationTokenSource _modeCts;          // cancels the running check only (settings changed / switched to picture only)
        private VideoCapture _liveCap;                     // the open camera, for PumpCommands

        /// <summary>
        /// True: the live view runs the check of its mode (read OCR / decode 2D / LED colours / blink / compare) the way a real job does -
        /// same settings, same overlays - again and again, with no time-out and no result file. False: just the picture.
        /// </summary>
        public bool RunMode { get { return _runMode; } }
        public void SetRunMode(bool on) { _runMode = on; RestartMode(); }

        /// <summary>Start the check again with the current CSV / files (after a setting, ROI or template changed). Only does something in run mode.</summary>
        public void RestartMode() {
            _restart = true;
            CancellationTokenSource c = _modeCts;
            if (c != null) { try { c.Cancel(); } catch { } }
        }

        /// <summary>Capture ONE fresh frame from the camera and show it.</summary>
        public void RequestFrame() { Post(cap => Capture(cap)); }

        /// <summary>Show the stored frame again (new zoom, ROI or colour test). Does nothing while streaming.</summary>
        public void Rerender() { Post(cap => { if (!_runMode && !_streaming && _snap != null) RenderSnap(); }); }

        public Rectangle Roi { get { lock (_vis) return _roi; } }
        public void SetRoi(Rectangle r) { lock (_vis) _roi = r; Rerender(); }

        public bool HsvTest { get { lock (_vis) return _hsvTest; } }
        public void SetHsvTest(bool on) { lock (_vis) _hsvTest = on; Rerender(); }

        /// <summary>Re-read ROI, rotation and colour range from the CSV (call after the CSV was edited).</summary>
        public void ReloadVision() { Post(cap => { LoadVision(); if (_runMode) RestartMode(); else if (!_streaming && _snap != null) RenderSnap(); }); }

        /// <summary>Saves a crop of the current picture (same orientation as the checks) to a PNG file.</summary>
        public void SaveCrop(Rectangle r, string path) {
            Post(cap => {
                using (Image<Bgr, byte> view = GrabView(cap)) {
                    if (view == null) return;
                    Rectangle rr = Rectangle.Intersect(r, new Rectangle(Point.Empty, view.Size));
                    if (rr.Width <= 0 || rr.Height <= 0) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                    using (Image<Bgr, byte> crop = view.Copy(rr)) crop.Save(path);
                }
            });
        }

        /// <summary>
        /// Like SaveCrop, but the picture is first carried back to the TAUGHT position with n (live -> taught, from the tracker), and the
        /// rectangle is in taught coordinates. Used when a symbol is drawn again while the workpiece / zoom is not where the others were taught.
        /// </summary>
        public void SaveCropWarped(double[] n, Rectangle taughtRect, string path) {
            Post(cap => {
                using (Image<Bgr, byte> view = GrabView(cap)) {
                    if (view == null) return;
                    var al = new SymbolAligner(); al.N = (double[])n.Clone();
                    using (Mat norm = al.Normalize(view.Mat)) {
                        Rectangle rr = Rectangle.Intersect(taughtRect, new Rectangle(Point.Empty, norm.Size));
                        if (rr.Width <= 0 || rr.Height <= 0) return;
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                        using (Mat crop = new Mat(norm, rr)) CvInvoke.Imwrite(path, crop);
                    }
                }
            });
        }

        /// <summary>Tries to read a 2D code inside the rectangle (like the old ROI test). done(null) = nothing found.</summary>
        public void DecodeCrop(Rectangle r, Action<string> done) {
            Post(cap => {
                string text = null;
                try {
                    using (Image<Bgr, byte> view = GrabView(cap)) {
                        if (view != null) {
                            Rectangle rr = Rectangle.Intersect(r, new Rectangle(Point.Empty, view.Size));
                            if (rr.Width > 0 && rr.Height > 0)
                                using (Image<Bgr, byte> crop = view.Copy(rr)) {
                                    Result res = new BarcodeReader().Decode(crop.Bitmap);
                                    if (res != null) text = res.ToString();
                                }
                        }
                    }
                } catch { }
                try { done(text); } catch { }
            });
        }

        // ── Preview thread ───────────────────────────────────────────────────

        private void Loop() {
            VideoCapture cap = null;
            bool opened = false;
            try {
                Status("Opening camera");
                StepConfig cfg = StepConfig.Load(StepName);
                try { Skip.Load(AppFilePath.Folder + Head); } catch { }

                cap = CameraWorker.OpenCapture(cfg, Head, Port, Skip, _cts.Token);
                opened = true;
                LoadVision();
                RaiseOpened(true, null);
                Status("Live (setup) - port " + Port);

                _liveCap = cap;
                while (!_cts.IsCancellationRequested) {
                    if (_runMode) RunModeLoop(cap); else PictureLoop(cap);
                }
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                if (!opened) RaiseOpened(false, ex.Message);
                Status("FAIL: " + ex.Message);
            } finally {
                _liveCap = null;
                try { if (_snap != null) _snap.Dispose(); } catch { }
                try { if (cap != null) cap.Dispose(); } catch { }
            }
        }

        /// <summary>Runs the commands posted from the UI (ROI crop, camera values, driver window ...) on this thread with the live camera.</summary>
        private void PumpCommands() {
            VideoCapture cap = _liveCap;
            Action<VideoCapture> cmd;
            while (_cmds.TryDequeue(out cmd)) { try { cmd(cap); } catch { } }
        }

        /// <summary>Plain picture (setup): continuous video, or one frame at a time.</summary>
        private void PictureLoop(VideoCapture cap) {
            bool wasStreaming = true;
            while (!_cts.IsCancellationRequested && !_runMode) {
                PumpCommands();
                if (_streaming) {
                    wasStreaming = true;
                    using (Image<Bgr, byte> view = GrabView(cap)) {
                        if (view == null) { Thread.Sleep(20); continue; }
                        Send(view);
                    }
                    Thread.Sleep(FrameIntervalMs);
                } else {
                    if (wasStreaming) { wasStreaming = false; Capture(cap); }   // leave one picture on screen
                    Thread.Sleep(30);                                           // nothing is sent until asked
                }
            }
        }

        /// <summary>
        /// Runs the check of this mode with the real worker code on the open camera. When the mode cannot run (no template, no
        /// settings ...) the plain picture is shown instead, with the reason, until a setting changes.
        /// </summary>
        private void RunModeLoop(VideoCapture cap) {
            while (!_cts.IsCancellationRequested && _runMode) {
                _restart = false;
                string error = null;
                using (var modeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token)) {
                    _modeCts = modeCts;
                    if (_restart) modeCts.Cancel();               // a change arrived while we were setting up
                    try {
                        CameraWorker worker = CreateModeWorker(modeCts.Token, out error);
                        if (worker != null) worker.RunTest(cap, PumpCommands);     // blocks until cancelled
                    } catch (OperationCanceledException) {
                    } catch (Exception ex) {
                        error = ex.Message;
                    } finally {
                        _modeCts = null;
                    }
                }
                if (_cts.IsCancellationRequested) return;
                if (error != null && _runMode && !_restart) {
                    CameraHub.Log("live " + Head + " " + StepName + ": mode cannot run - " + error);
                    Status("Live (picture only) - " + error);
                    PictureUntilRestart(cap);
                }
            }
        }

        private void PictureUntilRestart(VideoCapture cap) {
            while (!_cts.IsCancellationRequested && _runMode && !_restart) {
                PumpCommands();
                using (Image<Bgr, byte> view = GrabView(cap)) {
                    if (view == null) { Thread.Sleep(20); continue; }
                    Send(view);
                }
                Thread.Sleep(FrameIntervalMs);
            }
        }

        /// <summary>The same worker a real job uses, wired to this tile.</summary>
        private CameraWorker CreateModeWorker(CancellationToken token, out string error) {
            error = null;
            string expected = "";                                  // OCR: the text of line 2 of test_head_N_steptest.txt
            try {
                string[] lines = File.ReadAllLines(AppPaths.ConfigDir + "test_head_" + Head + "_steptest.txt");
                if (lines.Length > 1) expected = lines[1];
            } catch { }

            string reject;
            JobSpec job = JobSpec.FromStepName(Head, StepName, expected, out reject);
            if (job == null) { error = string.IsNullOrEmpty(reject) ? "this mode has no check to run" : reject; return null; }
            if (job.Kind == JobKind.CheckOcr && string.IsNullOrEmpty(job.ExpectedText))
                job.ExpectedText = "\u0001";                       // never matches: the tile just shows what is being read
            job.PortOverride = Port;

            var w = new CameraWorker(job, token);
            w.FrameReady    += (h, bmp) => { Action<int, Bitmap> f = FrameReady; if (f != null) f(h, bmp); else bmp.Dispose(); };
            w.StatusChanged += (h, t) => Status(t == "PASS" ? t : "Live - " + t);
            return w;
        }

        /// <summary>Grabs a fresh frame (the driver may first hand out an old buffered one) and shows it.</summary>
        private void Capture(VideoCapture cap) {
            Image<Bgr, byte> fresh = null;
            for (int i = 0; i < 3; i++) {
                Image<Bgr, byte> v = GrabView(cap);
                if (v == null) continue;
                if (fresh != null) fresh.Dispose();
                fresh = v;
            }
            if (fresh == null) { Status("Capture failed - no frame"); return; }
            if (_snap != null) _snap.Dispose();
            _snap = fresh;
            RenderSnap();
            Status("Live (1 frame " + DateTime.Now.ToString("HH:mm:ss") + ") - port " + Port);
        }

        private void RenderSnap() {
            if (_snap == null) return;
            using (Image<Bgr, byte> copy = _snap.Clone()) Send(copy);      // Send draws on its image, so never on the stored one
        }

        /// <summary>One frame in the orientation the checks use. Caller disposes. Null = no frame.</summary>
        private Image<Bgr, byte> GrabView(VideoCapture cap) {
            Mat raw = null;
            try { raw = cap.QueryFrame(); } catch { }
            using (Mat frame = AutoResolution.Normalize(Head, raw)) {          // same size a real job sees, so a drawn ROI matches
                if (frame == null || frame.IsEmpty) return null;
                using (Image<Bgr, byte> bgr = frame.ToImage<Bgr, byte>()) {
                    double deg; lock (_vis) deg = _rotate;
                    return deg == 0 ? bgr.Clone() : bgr.Rotate(deg, new Bgr());
                }
            }
        }

        private void LoadVision() {
            try {
                StepConfig cfg = StepConfig.Load(StepName);
                int h = Head;

                Rectangle roi = Rectangle.Empty;
                try {
                    roi = new Rectangle(cfg.GetInt(h, HeadConfigKey.RectX), cfg.GetInt(h, HeadConfigKey.RectY),
                                        cfg.GetInt(h, HeadConfigKey.RectWidth), cfg.GetInt(h, HeadConfigKey.RectHeight));
                } catch { }

                double rot = 0;
                if (StepName.IndexOf(CameraMode.CheckOCR, StringComparison.OrdinalIgnoreCase) >= 0) rot = 180;
                else if (StepName.IndexOf(CameraMode.Read2d, StringComparison.OrdinalIgnoreCase) >= 0)
                    rot = cfg.GetIntOr(h, HeadConfigKey.AdjustDegree, 0);

                bool hasColor = false, hsvFlag = false; int mask = 0;
                Hsv hl = new Hsv(), hh = new Hsv(); Bgr bl = new Bgr(), bh = new Bgr();
                try {
                    hsvFlag = cfg.GetBool(h, HeadConfigKey.HsvFlag);
                    mask    = cfg.GetInt(h, HeadConfigKey.HsvMask);
                    string[] p = cfg.Get(h, HeadConfigKey.HsvFormat).Split(' ');
                    var v = new int[6];
                    for (int i = 0; i < 6; i++) v[i] = Convert.ToInt32(p[i]);
                    if (hsvFlag) { hl = new Hsv(v[0], v[2], v[4]); hh = new Hsv(v[1], v[3], v[5]); }
                    else         { bl = new Bgr(v[0], v[2], v[4]); bh = new Bgr(v[1], v[3], v[5]); }
                    hasColor = true;
                } catch { }

                lock (_vis) {
                    _roi = roi; _rotate = rot;
                    _hasColor = hasColor; _hsvFlag = hsvFlag; _hsvMask = mask;
                    _hsvLow = hl; _hsvHigh = hh; _bgrLow = bl; _bgrHigh = bh;
                }
            } catch { }
        }

        private void Send(Image<Bgr, byte> view) {
            Action<int, Bitmap> handler = FrameReady;
            if (handler == null) return;

            Rectangle roi; bool test, hasColor, hsvFlag; int need; Hsv hl, hh; Bgr bl, bh;
            lock (_vis) {
                _frameSize = view.Size;
                roi = _roi; test = _hsvTest; hasColor = _hasColor; hsvFlag = _hsvFlag; need = _hsvMask;
                hl = _hsvLow; hh = _hsvHigh; bl = _bgrLow; bh = _bgrHigh;
            }
            Rectangle r = Rectangle.Intersect(roi, new Rectangle(Point.Empty, view.Size));

            if (test && hasColor && r.Width > 0 && r.Height > 0) {
                // Same colour test as the LED check: matching pixels inside the ROI are painted green.
                int count;
                using (Image<Bgr, byte> crop = view.Copy(r)) {
                    Image<Gray, byte> mask;
                    if (hsvFlag) { using (Image<Hsv, byte> hsvImg = crop.Convert<Hsv, byte>()) mask = hsvImg.InRange(hl, hh); }
                    else         { mask = crop.InRange(bl, bh); }
                    using (mask) {
                        count = mask.CountNonzero()[0];
                        view.ROI = r;
                        try { view.SetValue(new Bgr(0, 255, 0), mask); } finally { view.ROI = Rectangle.Empty; }
                    }
                }
                bool ok = count >= need;
                CvInvoke.PutText(view, "pixels=" + count + "  need>=" + need + (ok ? "  OK" : "  low"),
                    new Point(20, 60), FontFace.HersheySimplex, 0.8,
                    ok ? new MCvScalar(0, 255, 0) : new MCvScalar(0, 0, 255), 2);
            }

            if (r.Width > 0 && r.Height > 0) CvInvoke.Rectangle(view, r, new MCvScalar(0, 255, 255), 2);
            CvInvoke.PutText(view, "Head " + Head + "  port " + Port + "  " + view.Width + "x" + view.Height + "  [" + StepName + "]",
                new Point(20, 30), FontFace.HersheySimplex, 0.7, new MCvScalar(0, 255, 0), 2);

            // Independent copy of the part the operator zoomed into (see TileView.Make).
            Bitmap copy;
            try { copy = TileView.Make(Head, view, PreviewMaxWidth); } catch { return; }
            handler(Head, copy);
        }

        private void Status(string text) {
            Action<int, string> h = StatusChanged;
            if (h != null) { try { h(Head, text); } catch { } }
        }

        private void RaiseOpened(bool ok, string error) {
            Action<LivePreview, bool, string> h = Opened;
            if (h != null) { try { h(this, ok, error); } catch { } }
        }
    }
}
