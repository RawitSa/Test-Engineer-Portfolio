using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace camera_show {
    /// <summary>
    /// Thread-safe, read-only loader for config/cameraStep_{mode}.csv ("Head 1 Port,0" per line).
    /// Replaces SetupPay for the parallel runner: SetupPay keeps a "selected tab" as shared state
    /// and is not safe to call from several threads. The file is read once, then only read from memory.
    /// </summary>
    public sealed class StepConfig {
        public static readonly string ConfigDir = AppPaths.ConfigDir;

        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static StepConfig Load(string modeName) {
            string path = ConfigDir + "cameraStep_" + modeName + ".csv";
            var cfg = new StepConfig();
            // FileShare.ReadWrite: other tools (Excel, fMain) may have the file open.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs)) {
                string line;
                while ((line = sr.ReadLine()) != null) {
                    int i = line.IndexOf(',');
                    if (i <= 0) continue;
                    cfg._values[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            return cfg;
        }

        /// <param name="suffix">One of HeadConfigKey.* (leading space included).</param>
        public string Get(int head, string suffix) {
            string key = HeadConfigKey.Head + head + suffix;
            string v;
            if (!_values.TryGetValue(key, out v))
                throw new KeyNotFoundException("Missing config key: '" + key + "'");
            return v;
        }

        public bool TryGet(int head, string suffix, out string value) =>
            _values.TryGetValue(HeadConfigKey.Head + head + suffix, out value);

        public int GetIntOr(int head, string suffix, int fallback) {
            string v; int n;
            return TryGet(head, suffix, out v) && int.TryParse(v, out n) ? n : fallback;
        }

        public bool GetBoolOr(int head, string suffix, bool fallback) {
            string v; bool b;
            return TryGet(head, suffix, out v) && bool.TryParse(v, out b) ? b : fallback;
        }

        public int    GetInt (int head, string suffix) => Convert.ToInt32(Get(head, suffix));
        public bool   GetBool(int head, string suffix) => Convert.ToBoolean(Get(head, suffix));

        // ── Writing (used by PortResolver and the tile right-click menu; serialised by WriteLock) ─────

        /// <summary>
        /// Replaces the value of one "key,value" line in a cameraStep CSV, keeping every other line
        /// and the file's line endings untouched. Returns false when the key is not in the file.
        /// </summary>
        private static readonly object WriteLock = new object();

        public static bool WriteValueFile(string path, int head, string suffix, string value) {
            lock (WriteLock) return WriteValueFileLocked(path, head, suffix, value);
        }

        private static bool WriteValueFileLocked(string path, int head, string suffix, string value) {
            string key = HeadConfigKey.Head + head + suffix;
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();

            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            bool found = false;
            for (int i = 0; i < lines.Length; i++) {
                int c = lines[i].IndexOf(',');
                if (c > 0 && string.Equals(lines[i].Substring(0, c).Trim(), key, StringComparison.OrdinalIgnoreCase)) {
                    lines[i] = lines[i].Substring(0, c + 1) + value;
                    found = true;
                    break;
                }
            }
            if (!found) return false;
            File.WriteAllText(path, string.Join(nl, lines), new System.Text.UTF8Encoding(false));
            return true;
        }

        // ── Which CSV decides the camera port ────────────────────────────────

        /// <summary>All cameraStep_*.csv mode names in the config folder.</summary>
        public static List<string> ListModes() {
            try {
                return Directory.GetFiles(ConfigDir, "cameraStep_*.csv")
                    .Select(f => Path.GetFileNameWithoutExtension(f).Substring("cameraStep_".Length))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            } catch { return new List<string>(); }
        }

        public static bool IsSetPortMode(string mode) {
            return mode != null && mode.EndsWith("_SetPort", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// config/camera_show_port_source.txt holds one mode name (e.g. read2d). When it is set, Port / Set Address /
        /// Gamma of EVERY mode are taken from that mode's CSV, so a head cannot end up on a different camera in
        /// different modes. The *_SetPort mode is exempt: it deliberately probes its own ports. Null = not used.
        /// </summary>
        public static string PortSource(string stepName) {
            if (stepName == null || IsSetPortMode(stepName)) return null;
            try {
                string t = File.ReadAllText(ConfigDir + "camera_show_port_source.txt").Trim();
                if (t.Length > 0 && File.Exists(ConfigDir + "cameraStep_" + t + ".csv")) return t;
            } catch { }
            return null;
        }

        /// <summary>The mode whose CSV holds the port of this step (its own, unless a port source is configured).</summary>
        public static string PortStep(string stepName) { return PortSource(stepName) ?? stepName; }

        /// <summary>Where a port chosen by the operator must be written so every mode agrees.</summary>
        public static List<string> PortWriteTargets(string stepName) {
            var list = new List<string>();
            if (stepName == null) return list;
            string src = PortSource(stepName);
            if (src != null) { list.Add(src); return list; }
            list.Add(stepName);
            if (IsSetPortMode(stepName)) return list;
            foreach (string m in ListModes()) if (!IsSetPortMode(m) && !list.Contains(m, StringComparer.OrdinalIgnoreCase)) list.Add(m);
            return list;
        }

        /// <summary>
        /// Null when every mode (except *_SetPort) gives this head the same port - or a port source is set.
        /// Otherwise a text like "port 0 in check_led_blue, check_led_green; port 3 in read2d".
        /// </summary>
        public static string PortMismatch(int head) {
            if (PortSource("x") != null) return null;
            var byPort = new Dictionary<int, List<string>>();
            foreach (string mode in ListModes()) {
                if (IsSetPortMode(mode)) continue;
                try {
                    StepConfig cfg = Load(mode);
                    int port = cfg.GetIntOr(head, HeadConfigKey.Port, -1);
                    if (port < 0) continue;
                    string a; int g = cfg.GetIntOr(head, HeadConfigKey.Gamma, -1);
                    bool portMode = cfg.TryGet(head, HeadConfigKey.SetAddress, out a)
                        ? string.Equals(a, CameraAddress.Port, StringComparison.OrdinalIgnoreCase) || g == -1 : g == -1;
                    if (!portMode) continue;
                    List<string> l; if (!byPort.TryGetValue(port, out l)) byPort[port] = l = new List<string>();
                    l.Add(mode);
                } catch { }
            }
            if (byPort.Count <= 1) return null;
            return string.Join("; ", byPort.OrderBy(kv => kv.Key).Select(kv => "port " + kv.Key + " in " + string.Join(", ", kv.Value)));
        }

        public static bool WriteValue(string modeName, int head, string suffix, string value) =>
            WriteValueFile(ConfigDir + "cameraStep_" + modeName + ".csv", head, suffix, value);
    }
}
