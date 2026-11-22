using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace camera_show {
    /// <summary>
    /// Where the config folder is and where result files are written - decided ONCE at start, so the exe keeps
    /// working when another project (the "host") puts it in a different folder.
    ///
    /// Config folder, first match wins (a folder counts when it holds head.txt or a cameraStep_*.csv):
    ///   1. environment variable CAMERA_SHOW_CONFIG
    ///   2. "config=" in camera_show_paths.txt next to the exe (relative paths are relative to the exe's folder)
    ///   3. ../../config/ from the current directory      (the original layout: host\bin\Debug -> host\config)
    ///   4. ../config/    from the exe's folder           (exe in host\mini_projeck -> host\config)
    ///   5. ../../config/ and ./config/ from the exe's folder, ./config/ from the current directory
    ///   6. a "config" folder found while walking up from the current directory, then from the exe's folder
    ///
    /// Output files (test_head_N_result.txt, call_exe_tric.txt, camera_show_hub.log ...):
    ///   "output=" in camera_show_paths.txt, or CAMERA_SHOW_OUTPUT; otherwise the CURRENT DIRECTORY, because the
    ///   host starts the exe without a working directory and reads the result files from its own one.
    ///
    /// camera_show_paths.txt (all lines optional, '#' = comment):
    ///   config=../config
    ///   output=
    /// </summary>
    public static class AppPaths {
        /// <summary>Folder of the running exe, ends with '/'.</summary>
        public static readonly string ExeDir;
        /// <summary>Config folder as a full path, ends with '/'. Concatenate file names directly.</summary>
        public static readonly string ConfigDir;
        /// <summary>"" (= current directory) or a full path ending with '/'.</summary>
        public static readonly string OutputDir;
        /// <summary>One line saying how the config folder was found (written to the hub log).</summary>
        public static readonly string Description;

        private const string SettingsFile = "camera_show_paths.txt";

        static AppPaths() {
            ExeDir = Norm(AppDomain.CurrentDomain.BaseDirectory);
            string cwd = Norm(Directory.GetCurrentDirectory());

            string cfgSetting = null, outSetting = null;
            try {
                string f = ExeDir + SettingsFile;
                if (File.Exists(f))
                    foreach (string raw in File.ReadAllLines(f)) {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq < 1) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string val = line.Substring(eq + 1).Trim();
                        if (key == "config") cfgSetting = val;
                        else if (key == "output") outSetting = val;
                    }
            } catch { }

            string how = null, found = null;
            foreach (var c in Candidates(cwd, cfgSetting)) {
                if (LooksLikeConfig(c.Item1)) { found = c.Item1; how = c.Item2; break; }
            }
            if (found == null) { found = Norm(Path.GetFullPath(Path.Combine(cwd, "../../config"))); how = "default (nothing found, original ../../config/)"; }
            ConfigDir = found;
            Description = "config = " + ConfigDir + "   [" + how + "]";

            string o = Environment.GetEnvironmentVariable("CAMERA_SHOW_OUTPUT");
            if (string.IsNullOrWhiteSpace(o)) o = outSetting;
            if (string.IsNullOrWhiteSpace(o)) OutputDir = "";
            else {
                try {
                    string full = Norm(Path.GetFullPath(Path.IsPathRooted(o) ? o : Path.Combine(ExeDir, o)));
                    Directory.CreateDirectory(full);
                    OutputDir = full;
                } catch { OutputDir = ""; }
            }
        }

        private static IEnumerable<Tuple<string, string>> Candidates(string cwd, string cfgSetting) {
            string env = Environment.GetEnvironmentVariable("CAMERA_SHOW_CONFIG");
            if (!string.IsNullOrWhiteSpace(env)) yield return T(Rel(cwd, env), "environment CAMERA_SHOW_CONFIG");
            if (!string.IsNullOrWhiteSpace(cfgSetting)) yield return T(Rel(ExeDir, cfgSetting), SettingsFile);

            yield return T(Rel(cwd, "../../config"),    "../../config from the current directory");
            yield return T(Rel(ExeDir, "../config"),    "../config from the exe folder");
            yield return T(Rel(ExeDir, "../../config"), "../../config from the exe folder");
            yield return T(Rel(ExeDir, "config"),       "config next to the exe");
            yield return T(Rel(cwd, "config"),          "config in the current directory");

            foreach (string start in new[] { cwd, ExeDir }) {
                string d = start;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(d); i++) {
                    yield return T(Rel(d, "config"), "config found walking up from " + start);
                    DirectoryInfo p = null;
                    try { p = Directory.GetParent(d.TrimEnd('/')); } catch { }
                    d = p == null ? null : Norm(p.FullName);
                }
            }
        }

        private static Tuple<string, string> T(string a, string b) { return Tuple.Create(a, b); }

        private static string Rel(string baseDir, string path) {
            try { return Norm(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path))); }
            catch { return ""; }
        }

        private static bool LooksLikeConfig(string dir) {
            try {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                return File.Exists(dir + "head.txt") || Directory.GetFiles(dir, "cameraStep_*.csv").Length > 0;
            } catch { return false; }
        }

        private static string Norm(string p) {
            if (string.IsNullOrEmpty(p)) return p;
            p = p.Replace('\\', '/');
            return p.EndsWith("/") ? p : p + "/";
        }
    }
}
