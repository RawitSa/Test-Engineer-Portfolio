using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace camera_show {
    /// <summary>
    /// The one long-lived object that owns all camera jobs.
    ///   * Submit(job)  starts that head IMMEDIATELY (own thread, own camera) - it never waits for other heads.
    ///   * At most MaxCameras run at once; extra jobs wait in a queue and start as soon as a slot frees up.
    ///   * A head that is still running keeps its job; a newer request for the same head waits behind it
    ///     (only the latest waiting request per head is kept).
    ///   * StartWatching() turns "the other program wrote test_head_N_steptest.txt" into Submit() calls.
    /// </summary>
    public sealed class CameraHub : IDisposable {
        public const int MaxCameras = 8;
        private const int PollMs = 250;
        private static readonly Regex StepFile = new Regex(@"^test_head_(\d+)_steptest\.txt$", RegexOptions.IgnoreCase);
        private static readonly object LogLock = new object();

        private readonly object _lock = new object();
        private readonly HashSet<int> _running = new HashSet<int>();
        private readonly List<JobSpec> _queue = new List<JobSpec>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Dictionary<int, long> _handled = new Dictionary<int, long>();   // head -> mtime ticks already submitted
        private readonly Dictionary<int, long> _stable  = new Dictionary<int, long>();   // head -> mtime seen on previous poll
        private readonly Dictionary<int, KeyValuePair<string, DateTime>> _lastSubmit =
            new Dictionary<int, KeyValuePair<string, DateTime>>();                    // head -> (what, when): duplicate guard
        private string _headFile;
        private bool _watchFiles = true;                 // head.txt / steptest polling (off when launched by the host)
        private string _inboxDir;                        // requests forwarded by later launches of the exe
        private string _headFileHandled, _headFileSeen;                                // "mtime|length" of head.txt
        private Timer _poll;
        private int _polling;
        private string _watchDir;

        public event Action<JobSpec>        JobStarted;
        public event Action<JobResult>      JobFinished;
        public event Action<int, Bitmap>    FrameReady;     // receiver must Dispose the bitmap
        public event Action<int, string>    StatusChanged;

        public CancellationToken Token => _cts.Token;

        /// <summary>
        /// Remote-desktop mode: job pictures are sent rarely and live setup views only show one frame when asked.
        /// Set it with <see cref="SetRemoteMode"/>.
        /// </summary>
        public static volatile bool RemoteMode;

        // The port each head really opened last (jobs and setup views), for the tile titles.
        private readonly Dictionary<int, int> _ports = new Dictionary<int, int>();
        public int PortOf(int head) { lock (_lock) { int p; return _ports.TryGetValue(head, out p) ? p : -1; } }
        private void NotePort(int head, int port) { lock (_lock) _ports[head] = port; }

        public void SetRemoteMode(bool on) {
            RemoteMode = on;
            LivePreview[] all;
            lock (_lock) all = _previews.Values.ToArray();
            foreach (LivePreview pv in all) pv.Streaming = !on;      // on: keep the last picture, send one when asked
        }

        public CameraHub() {
            // A head that only shows a setup preview gives its port to whoever asks for it.
            PortResolver.PreemptPreview = PreemptPreview;
        }

        // ── Live setup previews (right-click menu on a tile) ─────────────────

        private readonly Dictionary<int, LivePreview> _previews = new Dictionary<int, LivePreview>();
        /// <summary>A preview ended (stopped, preempted or replaced): the tile should clear its picture.</summary>
        public event Action<int> PreviewStopped;

        public bool IsRunning(int head)    { lock (_lock) return _running.Contains(head); }
        public bool IsPreviewing(int head) { lock (_lock) return _previews.ContainsKey(head); }
        public LivePreview GetPreview(int head) { lock (_lock) { LivePreview p; return _previews.TryGetValue(head, out p) ? p : null; } }
        public int PreviewPort(int head)   { LivePreview p = GetPreview(head); return p == null ? -1 : p.Port; }

        /// <summary>
        /// Opens a live view for one head. explicitPort >= 0 = the operator picked that port (saved to the CSV once it
        /// opens); -1 = find the port like a job would (Port / Gamma rules). Blocks while the port is looked up, so call
        /// it off the UI thread. A head that was previewing is closed first, and a head that only previews the wanted
        /// port is reset so the caller gets it.
        /// </summary>
        public bool StartPreview(int head, string step, int explicitPort, bool persistPort, bool streaming, out string error) {
            return StartPreview(head, step, explicitPort, persistPort, streaming, false, out error);     // picture only, as before
        }

        /// <summary>
        /// runMode = true: the live view runs the check of its mode (read OCR / decode 2D / LED / blink / compare) like a normal job,
        /// without result file or time-out. false: just the picture (setup).
        /// </summary>
        public bool StartPreview(int head, string step, int explicitPort, bool persistPort, bool streaming, bool runMode, out string error) {
            error = null;
            if (string.IsNullOrEmpty(step)) { error = "No mode selected for head " + head; return false; }
            if (IsRunning(head)) { error = "Head " + head + " is running a job"; return false; }

            StopPreview(head);                                  // camera of this head released first

            int port = explicitPort;
            if (port >= 0) {
                if (!PortResolver.ClaimPort(head, port, out error)) return false;
            } else {
                port = PortResolver.Acquire(new JobSpec { Head = head, StepName = step }, out error);
                if (port < 0) return false;
            }

            var pv = new LivePreview(head, step, port, persistPort && explicitPort >= 0);
            pv.Streaming = streaming && !RemoteMode;                // one frame at a time when asked for, or in remote mode
            pv.SetRunMode(runMode);
            NotePort(head, port);
            pv.FrameReady    += (h, bmp) => { var f = FrameReady; if (f != null) f(h, bmp); else bmp.Dispose(); };
            pv.StatusChanged += Status;
            pv.Opened        += OnPreviewOpened;
            lock (_lock) _previews[head] = pv;
            Log("preview head " + head + " " + step + " port " + port);
            pv.Start();
            return true;
        }

        private void OnPreviewOpened(LivePreview pv, bool ok, string error) {
            if (ok) {
                if (pv.PersistPort) {
                    bool saved = false;
                    bool addr = false;
                    try {
                        // The camera belongs to the head in every mode, so all modes (or the port source) get the same port.
                        foreach (string mode in StepConfig.PortWriteTargets(pv.StepName)) {
                            bool s1 = StepConfig.WriteValue(mode, pv.Head, HeadConfigKey.Port, pv.Port.ToString());
                            // The operator picked this port, so jobs must open exactly it - not scan by Gamma and land elsewhere.
                            bool s2 = StepConfig.WriteValue(mode, pv.Head, HeadConfigKey.SetAddress, CameraAddress.Port);
                            saved = saved || s1; addr = addr || s2;
                        }
                    } catch { }
                    // The camera's own identity too, so the head keeps THIS camera when Windows renumbers the ports after a restart.
                    bool bound = !StepConfig.IsSetPortMode(pv.StepName) && CameraRegistry.BindPort(pv.Head, pv.Port, true);
                    if (bound) saved = true;
                    Log("preview head " + pv.Head + " port " + pv.Port + (saved ? " saved to CSV" : " NOT saved to CSV") +
                        (addr ? ", Set Address = Port" : ""));
                    Status(pv.Head, saved ? "Live - port " + pv.Port + " saved (fixed)" : "Live - port " + pv.Port + " (CSV not written)");
                }
                return;
            }
            lock (_lock) { LivePreview cur; if (_previews.TryGetValue(pv.Head, out cur) && cur == pv) _previews.Remove(pv.Head); }
            PortResolver.Release(pv.Head);
            Log("preview head " + pv.Head + " port " + pv.Port + " failed: " + error);
            Status(pv.Head, "FAIL: " + error);
        }

        public void StopPreview(int head) {
            LivePreview pv;
            lock (_lock) {
                if (!_previews.TryGetValue(head, out pv)) return;
                _previews.Remove(head);
            }
            pv.Stop();                                          // joins the thread: the camera is free when this returns
            Raise(() => PreviewStopped?.Invoke(head));
        }

        public void StopAllPreviews() {
            int[] heads;
            lock (_lock) heads = _previews.Keys.ToArray();
            foreach (int h in heads) StopPreview(h);
        }

        private bool PreemptPreview(int head) {
            if (!IsPreviewing(head)) return false;              // running a real job: never taken away
            StopPreview(head);
            Status(head, "Port taken by another head - reset");
            return true;
        }

        // ── Submitting work ──────────────────────────────────────────────────

        public void Submit(JobSpec job) {
            lock (_lock) {
                _queue.RemoveAll(j => j.Head == job.Head);   // latest request for a head wins
                _queue.Add(job);
            }
            Log("submit " + job + (string.IsNullOrEmpty(job.ExpectedText) ? "" : " text='" + job.ExpectedText + "'"));
            Pump();
        }

        public bool IsIdle { get { lock (_lock) return _running.Count == 0 && _queue.Count == 0; } }

        public int RunningCount { get { lock (_lock) return _running.Count; } }

        /// <summary>Blocks until nothing is running or queued (used by batch mode).</summary>
        public void WaitIdle() {
            while (!IsIdle) Thread.Sleep(50);
        }

        /// <summary>Cancels every running job (each one still releases its camera and writes a FAIL result).</summary>
        public void CancelAll() { try { _cts.Cancel(); } catch { } }

        private void Pump() {
            var toStart = new List<JobSpec>();
            lock (_lock) {
                int i = 0;
                while (i < _queue.Count && _running.Count < MaxCameras) {
                    JobSpec j = _queue[i];
                    if (_running.Contains(j.Head)) { i++; continue; }   // same head still busy: keep waiting
                    _queue.RemoveAt(i);
                    _running.Add(j.Head);
                    toStart.Add(j);
                }
            }
            foreach (JobSpec j in toStart) StartJob(j);
        }

        private void StartJob(JobSpec job) {
            Raise(() => JobStarted?.Invoke(job));
            Task.Factory.StartNew(() => {
                JobResult result;
                try {
                    result = RunJob(job);
                } catch (Exception ex) {
                    result = new JobResult { Job = job, Pass = false, Message = "Error: " + ex.Message };
                    CameraWorker.WriteResult(result);
                } finally {
                    PortResolver.Release(job.Head);
                }
                Log("done   " + job + " -> " + (result.Pass ? "PASS" : "FAIL") + " [" + result.Message + "]");

                lock (_lock) _running.Remove(job.Head);
                Raise(() => JobFinished?.Invoke(result));
                Pump();   // a waiting job (4-camera limit, or a repeat request for this head) can start now
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private JobResult RunJob(JobSpec job) {
            StopPreview(job.Head);                // a head cannot preview and run a job on the same camera
            string mismatch = StepConfig.PortMismatch(job.Head);
            if (mismatch != null)
                Log("WARNING head " + job.Head + ": the CSV files disagree about its port (" + mismatch +
                    "). Put one mode name in camera_show_port_source.txt, or set the port again from the tile menu.");
            Status(job.Head, "Finding camera");
            string error;
            int port = PortResolver.Acquire(job, out error);
            if (port < 0) {
                var r = new JobResult { Job = job, Pass = false, Message = error };
                CameraWorker.WriteResult(r);
                string detail = PortResolver.TakeDetail(job.Head);
                Status(job.Head, "FAIL: " + error + (detail == null ? "" : "  |  " + detail));
                return r;
            }
            job.PortOverride = port;
            NotePort(job.Head, port);

            var worker = new CameraWorker(job, _cts.Token);
            worker.FrameReady    += (h, bmp) => { var f = FrameReady; if (f != null) f(h, bmp); else bmp.Dispose(); };
            worker.StatusChanged += Status;
            return worker.Run();
        }

        // ── Watching for config coming from the other program ───────────────

        /// <summary>
        /// Polls "{dir}/test_head_N_steptest.txt". Every time one of these files is (re)written the job
        /// for head N is submitted right away. Line 1 = step name, line 2 = OCR text.
        /// Files that already exist when watching starts are ignored unless <paramref name="runExisting"/>.
        /// </summary>
        public void StartWatching(string dir, bool runExisting) { StartPolling(dir, runExisting, true, null); }

        /// <summary>
        /// watchConfigFiles = true : react to head.txt and test_head_N_steptest.txt being written (manual --hub use).
        /// inboxDir != null        : also take requests that a later launch of the exe dropped there. The host starts
        ///                           the exe once per head, so every launch after the first only forwards its head.
        /// </summary>
        public void StartPolling(string dir, bool runExisting, bool watchConfigFiles, string inboxDir) {
            _watchDir = dir;
            _watchFiles = watchConfigFiles;
            _inboxDir = inboxDir;
            if (inboxDir != null) { try { Directory.CreateDirectory(inboxDir); } catch { } }

            if (watchConfigFiles) {
                foreach (string f in SafeList(dir)) {
                    Match m = StepFile.Match(Path.GetFileName(f));
                    if (m.Success) _handled[int.Parse(m.Groups[1].Value)] = runExisting ? 0 : File.GetLastWriteTimeUtc(f).Ticks;
                }
                _headFile = Path.Combine(dir, "head.txt");
                _headFileHandled = runExisting ? null : HeadFileStamp();
            }
            Log("config folder: " + AppPaths.Description);
            Log("watching " + Path.GetFullPath(dir) + (watchConfigFiles ? (runExisting ? " (existing files will run)" : "") : " (inbox only)"));
            _poll = new Timer(_ => Poll(), null, 0, PollMs);
        }

        private void Poll() {
            if (Interlocked.Exchange(ref _polling, 1) == 1) return;   // never overlap
            try {
                PollInbox();
                if (_watchFiles) PollHeadFile();
                foreach (string f in _watchFiles ? SafeList(_watchDir) : new string[0]) {
                    Match m = StepFile.Match(Path.GetFileName(f));
                    if (!m.Success) continue;
                    int head = int.Parse(m.Groups[1].Value);
                    long mtime = File.GetLastWriteTimeUtc(f).Ticks;

                    long done;
                    if (_handled.TryGetValue(head, out done) && done == mtime) { _stable.Remove(head); continue; }

                    // Debounce: act only when the timestamp was already the same one poll ago
                    // (the writer is finished), so we never read a half-written file.
                    long seen;
                    if (_stable.TryGetValue(head, out seen) && seen == mtime) {
                        _stable.Remove(head);
                        _handled[head] = mtime;
                        SubmitFromFile(head, f);
                    } else {
                        _stable[head] = mtime;
                    }
                }
            } catch (Exception ex) {
                Log("poll error: " + ex.Message);
            } finally {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        // ── head.txt: "start these heads now" ────────────────────────────────

        /// <summary>
        /// Heads listed in head.txt, separated by newline, space, comma or ';'  (e.g. "1 2 3 4").
        /// </summary>
        public static List<int> ParseHeads(string text) {
            var heads = new List<int>();
            foreach (string tok in (text ?? string.Empty).Split(new[] { '\r', '\n', ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) {
                int h;
                if (int.TryParse(tok.Trim(), out h) && h >= 1 && !heads.Contains(h)) heads.Add(h);
            }
            return heads;
        }

        public static List<int> ReadHeadFile(string dir) {
            try {
                using (var fs = new FileStream(Path.Combine(dir, "head.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs)) return ParseHeads(sr.ReadToEnd());
            } catch { return new List<int>(); }
        }

        private string HeadFileStamp() {
            try {
                var fi = new FileInfo(_headFile);
                return fi.Exists ? fi.LastWriteTimeUtc.Ticks + "|" + fi.Length : null;
            } catch { return null; }
        }

        private void PollHeadFile() {
            string stamp = HeadFileStamp();
            if (stamp == null || stamp == _headFileHandled) { _headFileSeen = null; return; }
            if (stamp != _headFileSeen) { _headFileSeen = stamp; return; }   // wait one poll: writer must be finished
            _headFileSeen = null;
            _headFileHandled = stamp;

            List<int> heads = ReadHeadFile(_watchDir);
            Log("head.txt -> [" + string.Join(",", heads) + "]");
            // Every head in the file starts now, side by side. Heads that are already running are left alone.
            foreach (int head in heads) SubmitHead(head, true);
        }

        // ── Requests forwarded by a later launch of the exe ──────────────────

        /// <summary>
        /// A request file holds head numbers ("1" or "1 2 3 4"). Deleting it is the receipt: the launcher waits for that
        /// before it acknowledges to the host (call_exe_tric.txt), so no head.txt value is ever lost to an overwrite.
        /// </summary>
        private void PollInbox() {
            if (_inboxDir == null) return;
            string[] files;
            try { files = Directory.GetFiles(_inboxDir, "*.req"); } catch { return; }
            Array.Sort(files, StringComparer.Ordinal);                      // oldest first (names start with the tick count)
            foreach (string f in files) {
                string text = null;
                try { text = File.ReadAllText(f); File.Delete(f); } catch { continue; }     // delete = claimed
                List<int> heads = ParseHeads(text);
                Log("inbox " + Path.GetFileName(f) + " -> [" + string.Join(",", heads) + "]");
                foreach (int h in heads) SubmitHead(h, false, false);        // an explicit launch is never a duplicate
            }
        }

        /// <summary>Starts head N from test_head_N_steptest.txt right now (used for the heads of the first launch).</summary>
        public void SubmitHeadConfig(int head) { SubmitHead(head, false, false); }

        public bool HasPreviews { get { lock (_lock) return _previews.Count > 0; } }

        // ── Reading a head's steptest file and submitting it ─────────────────

        private bool IsBusy(int head) {
            lock (_lock) return _running.Contains(head) || _queue.Exists(j => j.Head == head);
        }

        private void SubmitFromFile(int head, string path) { SubmitHead(head, false); }

        private void SubmitHead(int head, bool skipIfBusy, bool dedupe = true) {
            if (skipIfBusy && IsBusy(head)) { Log("head " + head + ": already running, ignored"); return; }

            string path = Path.Combine(_watchDir, "test_head_" + head + "_steptest.txt");
            if (!File.Exists(path)) { Reject(head, "", "No test_head_" + head + "_steptest.txt"); return; }

            string[] lines = null;
            for (int attempt = 0; attempt < 5 && lines == null; attempt++) {
                try {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs)) lines = sr.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                } catch (IOException) { Thread.Sleep(50); }
            }
            if (lines == null) { Log("head " + head + ": steptest file locked, will retry on next write"); return; }

            string reject;
            JobSpec job = JobSpec.FromSteptestLines(head, lines, out reject);
            if (job == null) {
                if (string.IsNullOrEmpty(reject)) { Log("head " + head + ": mode '" + (lines.Length > 0 ? lines[0].Trim() : "") + "' needs no camera job"); return; }
                Reject(head, lines[0].Trim(), reject);
                return;
            }

            // head.txt and steptest.txt are often written together: the second trigger must not run the same job twice.
            string what = job.StepName + "|" + job.ExpectedText;
            KeyValuePair<string, DateTime> last;
            if (dedupe && _lastSubmit.TryGetValue(head, out last) && last.Key == what && (DateTime.UtcNow - last.Value).TotalSeconds < 3) {
                Log("head " + head + ": duplicate trigger ignored"); return;
            }
            _lastSubmit[head] = new KeyValuePair<string, DateTime>(what, DateTime.UtcNow);
            Submit(job);
        }

        private void Reject(int head, string stepName, string reason) {
            Log("head " + head + ": rejected - " + reason);
            var r = new JobResult { Job = new JobSpec { Head = head, StepName = stepName }, Pass = false, Message = reason };
            CameraWorker.WriteResult(r);
            Status(head, "FAIL: " + reason);
        }

        private static string[] SafeList(string dir) {
            try { return Directory.GetFiles(dir, "test_head_*_steptest.txt"); } catch { return new string[0]; }
        }

        // ── Plumbing ─────────────────────────────────────────────────────────

        private void Status(int head, string text) { Raise(() => StatusChanged?.Invoke(head, text)); }

        /// <summary>A throwing UI handler must never kill a camera thread.</summary>
        private static void Raise(Action a) { try { a(); } catch { } }

        public static void Log(string line) {
            try {
                lock (LogLock) {
                    string path = AppPaths.OutputDir + "camera_show_hub.log";
                    try {   // keep the log from growing for ever: over 2 MB it becomes camera_show_hub.old.log (one old copy kept)
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > 2 * 1024 * 1024) {
                            string old = AppPaths.OutputDir + "camera_show_hub.old.log";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(path, old);
                        }
                    } catch { }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + line + "\r\n");
                }
            } catch { }
        }

        public void Dispose() {
            if (_poll != null) _poll.Dispose();
            CancelAll();
            StopAllPreviews();
            if (PortResolver.PreemptPreview == (Func<int, bool>)PreemptPreview) PortResolver.PreemptPreview = null;
            _cts.Dispose();
        }
    }
}
