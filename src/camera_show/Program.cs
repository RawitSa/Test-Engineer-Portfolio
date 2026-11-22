using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace camera_show {
    /// <summary>
    /// Lets the original single-camera Form1 (all the old setup menus) be started for ONE head
    /// without touching the shared head.txt / steptest files.
    /// </summary>
    internal static class SetupOverride {
        public static string Head;        // e.g. "2"
        public static string Step;        // e.g. "check_led"; null = read test_head_N_steptest.txt as usual
        public static bool   SetPort;     // true = show the "choose port" dialog on start
    }

    static class Program {
        /// <summary>
        /// No arguments = how the host project starts the exe (it writes config\\head.txt, then starts the exe once per head
        /// and waits for call_exe_tric.txt). By default this is the parallel hub:
        ///   * first launch  -> becomes the hub: starts the head(s) listed in head.txt at once, acknowledges the host
        ///                      (call_exe_tric.txt) and keeps watching for more;
        ///   * later launches -> hand their head(s) to the running hub, acknowledge, and exit at once;
        ///   * the hub closes itself after config\\camera_show_idle_exit.txt seconds without work (default 120, 0 = never).
        /// config\\camera_show_mode.txt containing "single" restores the original one-camera-per-process Form1.
        /// A head whose steptest mode the hub cannot run (Normal, MultipleLed) also goes to the original Form1.
        ///
        /// Setup of one camera with the old single-camera screens (set port, resolution, ROI, HSV ...):
        ///   camera_show.exe --setup 2 CheckOCR --set-port
        ///   Head and step are taken from the arguments, NOT from head.txt / steptest, so it works while
        ///   head.txt lists several heads. Values are saved to the same cameraStep_*.csv the hub reads.
        ///
        /// Parallel mode (up to 8 cameras at once; every job closes its camera when it is done):
        ///
        ///   camera_show.exe --hub [--run-existing]
        ///       Stays open and watches config\head.txt and config\test_head_N_steptest.txt.
        ///       head.txt may list several heads ("1 2 3 4" or one per line): all of them start together.
        ///       A steptest file written alone starts just that head. Nobody waits for anybody else.
        ///       (Starting with no argument while head.txt lists 2+ heads does the same, with --run-existing.)
        ///
        ///   camera_show.exe --multi 1=check_led 2=CheckOCR:ABC123 3=read2d 4=BlinkLed
        ///   camera_show.exe --multi-file jobs.txt [--headless]
        ///       Batch: start the listed jobs together, close when all are finished.
        ///
        /// Exit code (batch): 0 = every job PASS, 1 = at least one FAIL, 2 = bad arguments.
        /// Each job writes test_head_N_result.txt exactly like the single-camera app.
        /// </summary>
        [STAThread]
        static int Main(string[] args) {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0 && args[0] == "--setup") {
                int head;
                if (args.Length < 2 || !int.TryParse(args[1], out head) || head < 1) {
                    MessageBox.Show("Usage: camera_show.exe --setup HEAD [STEP] [--set-port]\n\n" +
                        "e.g.  --setup 2 CheckOCR --set-port", "camera_show", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 2;
                }
                SetupOverride.Head    = head.ToString();
                SetupOverride.Step    = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;
                SetupOverride.SetPort = args.Contains("--set-port");
                Application.Run(new Form1());
                return 0;
            }

            if (args.Length > 0 && args[0] == "--hub")
                return RunHub(args);
            if (args.Length > 0 && (args[0] == "--multi" || args[0] == "--multi-file"))
                return RunBatch(args);

            if (ReadModeFile() != "single")
                return RunAsHostLaunch();

            // "single": original behaviour. Several heads in head.txt can still only be served by the hub.
            if (CameraHub.ReadHeadFile(StepConfig.ConfigDir).Count > 1)
                return RunHub(new[] { "--hub", "--run-existing" });

            Application.Run(new Form1());
            return 0;
        }

        // ── Started by the host project ──────────────────────────────────────

        private static int RunAsHostLaunch() {
            List<int> heads = CameraHub.ReadHeadFile(StepConfig.ConfigDir);
            if (heads.Count == 0 || heads.All(h => !HubCanRun(h))) {          // nothing for the hub: original behaviour
                Application.Run(new Form1());
                return 0;
            }

            bool created;
            using (var mtx = new Mutex(true, "Local\\camera_show_hub", out created)) {
                bool owner = created;
                try {
                    if (!owner) {
                        if (Forward(heads)) { WriteTric(); return 0; }       // the running hub took the request
                        // nobody took it: the hub is closing or gone -> wait for the mutex and become the hub
                        try { owner = mtx.WaitOne(4000); } catch (AbandonedMutexException) { owner = true; }
                        if (!owner) { WriteTric(); return 3; }
                    }
                    return RunHostHub(heads);
                } finally {
                    if (owner) { try { mtx.ReleaseMutex(); } catch { } }
                }
            }
        }

        private static int RunHostHub(List<int> heads) {
            using (var hub = new CameraHub()) {
                try { File.WriteAllText(AppFilePath.List, string.Empty); } catch { }   // the hub ignores the list; keep it from going stale
                hub.StartPolling(StepConfig.ConfigDir, false, false, AppPaths.ConfigDir + "hub_inbox/");
                foreach (int h in heads) hub.SubmitHeadConfig(h);
                WriteTric();                                                           // head.txt consumed: the host may go on

                var form = new FormMultiView(hub, false, 0);
                int idle = IdleExitSeconds();
                if (idle > 0) {
                    var sw = Stopwatch.StartNew();
                    var timer = new System.Windows.Forms.Timer { Interval = 1000 };
                    timer.Tick += (s, e) => {
                        if (!hub.IsIdle || hub.HasPreviews) sw.Restart();
                        else if (sw.Elapsed.TotalSeconds >= idle) { timer.Stop(); form.Close(); }
                    };
                    timer.Start();
                }
                Application.Run(form);
            }
            return 0;
        }

        /// <summary>Drops the head(s) into the running hub's inbox; true once the hub has taken (deleted) the request.</summary>
        private static bool Forward(List<int> heads) {
            string dir = AppPaths.ConfigDir + "hub_inbox/";
            string req = dir + DateTime.UtcNow.Ticks.ToString("D20") + "_" + Process.GetCurrentProcess().Id;
            try {
                Directory.CreateDirectory(dir);
                File.WriteAllText(req + ".tmp", string.Join(" ", heads));
                File.Move(req + ".tmp", req + ".req");              // appears complete, never half written
            } catch { return false; }

            for (int i = 0; i < 100; i++) {                         // up to 5 s for the hub to claim it
                if (!File.Exists(req + ".req")) return true;
                Thread.Sleep(50);
            }
            try { File.Delete(req + ".req"); } catch { }
            return false;
        }

        /// <summary>call_exe_tric.txt is the host's "I have read head.txt" acknowledgement (Form1.GetHead writes it too).</summary>
        private static void WriteTric() {
            try { File.WriteAllText(AppFilePath.TricExe, string.Empty); } catch { }
        }

        private static bool HubCanRun(int head) {
            try {
                string[] lines = File.ReadAllLines(StepConfig.ConfigDir + "test_head_" + head + "_steptest.txt");
                string reject;
                if (JobSpec.FromSteptestLines(head, lines, out reject) != null) return true;
                if (lines.Length > 0 && lines[0].Contains(CameraMode.MultipleLed)) return false;   // only the original Form1 can run it
                return !string.IsNullOrEmpty(reject);   // e.g. MultipleLed (not supported in parallel): a clear FAIL result beats a silent timeout
            } catch { return false; }
        }

        private static string ReadModeFile() {
            try { return File.ReadAllText(StepConfig.ConfigDir + "camera_show_mode.txt").Trim().ToLowerInvariant(); }
            catch { return ""; }
        }

        private static int IdleExitSeconds() {
            try { return Math.Max(0, Convert.ToInt32(File.ReadAllText(StepConfig.ConfigDir + "camera_show_idle_exit.txt").Trim())); }
            catch { return 120; }
        }

        private static int RunHub(string[] args) {
            using (var hub = new CameraHub()) {
                hub.StartWatching(StepConfig.ConfigDir, args.Contains("--run-existing"));
                WriteTric();
                Application.Run(new FormMultiView(hub, false, 0));
            }
            return 0;
        }

        private static int RunBatch(string[] args) {
            List<JobSpec> jobs;
            try {
                if (args[0] == "--multi-file") {
                    if (args.Length < 2) throw new FormatException("--multi-file needs a file path");
                    jobs = JobSpec.LoadFile(args[1]);
                } else {
                    jobs = args.Skip(1).Where(a => !a.StartsWith("--")).Select(JobSpec.Parse).ToList();
                }
                if (jobs.Count == 0) throw new FormatException("no jobs given");
                if (jobs.Count > CameraHub.MaxCameras) throw new FormatException("at most " + CameraHub.MaxCameras + " jobs");
                var dup = jobs.GroupBy(j => j.Head).FirstOrDefault(g => g.Count() > 1);
                if (dup != null) throw new FormatException("head " + dup.Key + " appears twice");
            } catch (Exception ex) {
                MessageBox.Show(ex.Message + "\n\nUsage: camera_show.exe --multi 1=check_led 2=CheckOCR:TEXT 3=read2d 4=BlinkLed",
                    "camera_show", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            using (var hub = new CameraHub()) {
                bool anyFail = false;
                hub.JobFinished += r => { if (!r.Pass) anyFail = true; };
                foreach (JobSpec j in jobs) hub.Submit(j);

                if (args.Contains("--headless")) {
                    hub.WaitIdle();
                    return anyFail ? 1 : 0;
                }

                Environment.ExitCode = 1;
                Application.Run(new FormMultiView(hub, true, jobs.Count));
                return Environment.ExitCode;
            }
        }
    }
}
