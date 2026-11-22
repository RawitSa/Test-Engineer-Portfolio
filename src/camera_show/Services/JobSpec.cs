using System;
using System.Collections.Generic;
using System.IO;

namespace camera_show {
    public enum JobKind { CheckLed, CheckOcr, Read2d, BlinkLed, Compare, MultiLed }

    /// <summary>One inspection command: which head, which step/mode, and (for OCR) the expected text.</summary>
    public sealed class JobSpec {
        public int     Head         { get; set; }
        /// <summary>
        /// Raw step name, exactly what the single-camera app calls global.StepTest
        /// (e.g. "check_led", "CheckOCR", "read2d", "read2d_SetPort", "BlinkLed").
        /// It selects config/cameraStep_{StepName}.csv and the config/{StepName}/ folder.
        /// </summary>
        public string  StepName     { get; set; }
        public JobKind Kind         { get; set; }
        /// <summary>Expected text for OCR jobs (line 2 of test_head_N_steptest.txt).</summary>
        public string  ExpectedText { get; set; } = string.Empty;
        /// <summary>Fallback timeout in ms; test_head_N_timeout.txt wins when present.</summary>
        public int     TimeOutMs    { get; set; } = 10000;

        /// <summary>Camera index chosen by PortResolver just before the job starts (null = use the CSV port).</summary>
        public int?    PortOverride { get; set; }

        /// <summary>Same value as <see cref="StepName"/>; kept so the CSV / folder lookups read naturally.</summary>
        public string ModeName => StepName;

        public string ResultFile => AppPaths.OutputDir + "test_head_" + Head + "_result.txt";
        public override string ToString() => "Head " + Head + " " + StepName;

        // ── Mode detection (same priority order as Form1.CheckMode) ───────────

        /// <summary>
        /// Returns null and sets <paramref name="reject"/> when the step name is not a supported
        /// parallel mode. reject == "" means "nothing to do" (normal mode).
        /// </summary>
        public static JobSpec FromStepName(int head, string stepName, string expectedText, out string reject) {
            reject = null;
            string n = (stepName ?? string.Empty).Trim();
            var job = new JobSpec { Head = head, StepName = n };

            if      (n.Contains(CameraMode.Read2d))    job.Kind = JobKind.Read2d;
            else if (n.IndexOf("compar", StringComparison.OrdinalIgnoreCase) >= 0) job.Kind = JobKind.Compare;   // template compare (any step name containing "compar")
            else if (n.IndexOf("multi_led", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("multiled", StringComparison.OrdinalIgnoreCase) >= 0) job.Kind = JobKind.MultiLed;   // several LEDs at once (any step name containing "multi_led")
            else if (n.Contains(CameraMode.CheckLed))  job.Kind = JobKind.CheckLed;
            else if (n.Contains(CameraMode.BlinkLed))  job.Kind = JobKind.BlinkLed;
            else if (n.Contains(CameraMode.MultipleLed)) {
                reject = "MultipleLed is not supported in parallel mode"; return null;
            }
            else if (n.Contains(CameraMode.CheckOCR))  job.Kind = JobKind.CheckOcr;
            else { reject = string.Empty; return null; }   // normal / unknown: nothing to inspect

            // Only OCR uses line 2 of the steptest file (Read2D ignores it, exactly like the single-camera app).
            if (job.Kind == JobKind.CheckOcr) job.ExpectedText = (expectedText ?? string.Empty).Trim();
            return job;
        }

        /// <summary>Builds a job from the lines of test_head_N_steptest.txt (line 1 = mode, line 2 = OCR text).</summary>
        public static JobSpec FromSteptestLines(int head, string[] lines, out string reject) {
            string mode = lines.Length > 0 ? lines[0] : string.Empty;
            string text = lines.Length > 1 ? lines[1] : string.Empty;
            return FromStepName(head, mode, text, out reject);
        }

        // ── Command-line parsing ──────────────────────────────────────────────

        /// <summary>
        /// "HEAD=MODE" or "HEAD=MODE:TEXT". MODE aliases: led, ocr, read2d, blink; anything else is
        /// used as the raw step name (e.g. read2d_SetPort). TEXT may itself contain ':'.
        /// </summary>
        public static JobSpec Parse(string s) {
            if (string.IsNullOrWhiteSpace(s)) throw new FormatException("empty job");
            int eq = s.IndexOf('=');
            if (eq < 1) throw new FormatException("job must look like HEAD=MODE[:TEXT] -> " + s);

            int head;
            if (!int.TryParse(s.Substring(0, eq).Trim(), out head) || head < 1)
                throw new FormatException("bad head number in job: " + s);

            string rest = s.Substring(eq + 1);
            int colon = rest.IndexOf(':');
            string mode = (colon < 0 ? rest : rest.Substring(0, colon)).Trim();
            string text = colon < 0 ? string.Empty : rest.Substring(colon + 1);

            switch (mode.ToLowerInvariant()) {
                case "led": case "checkled": mode = CameraMode.CheckLed; break;
                case "ocr":                  mode = CameraMode.CheckOCR; break;
                case "2d":                   mode = CameraMode.Read2d;   break;
                case "blink":                mode = CameraMode.BlinkLed; break;
            }

            string reject;
            JobSpec job = FromStepName(head, mode, text, out reject);
            if (job == null) throw new FormatException("job not runnable (" + (string.IsNullOrEmpty(reject) ? "unknown mode" : reject) + "): " + s);
            return job;
        }

        public static List<JobSpec> LoadFile(string path) {
            var list = new List<JobSpec>();
            foreach (string raw in File.ReadAllLines(path)) {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                list.Add(Parse(line));
            }
            return list;
        }
    }

    public sealed class JobResult {
        public JobSpec Job     { get; set; }
        public bool    Pass    { get; set; }
        public string  Message { get; set; } = string.Empty;
    }
}
