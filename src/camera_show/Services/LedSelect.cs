using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace camera_show {
    /// <summary>
    /// Which LEDs of a Multi-LED mode must be ON (and, if wanted, OFF). Read from config\{mode}\{head}led_select.txt
    /// (this head only) or config\{mode}\led_select.txt (every head); no file = every taught LED must be ON.
    ///
    ///   on=1-3,5-7,9-10      LED 1,2,3,5,6,7,9,10 must be lit; LED 4 and 8 are not looked at
    ///   on=all               every taught LED must be lit
    ///   off=4,8              (optional) these LEDs must be dark
    ///   hold_ms=500          (optional) how long they must stay like that; default = hold time of the colour settings
    /// Lines starting with # are comments. Numbers are the LED numbers of the ROI menu (LED 1 = Image1.png / line 1 of the Rectangle files).
    /// </summary>
    public sealed class LedSelect {
        public bool         All = true;                          // on=all (also when there is no file)
        public SortedSet<int> On  = new SortedSet<int>();
        public SortedSet<int> Off = new SortedSet<int>();
        public int          HoldMs = -1;                         // -1 = use the colour settings
        public string       Error;                               // null = fine
        public string       Source = "(no led_select.txt: every taught LED must be on)";

        public const int MaxLed = 100;

        public static string PathFor(string stepFolder, int head) { return stepFolder + head + "led_select.txt"; }

        public static LedSelect Load(string stepFolder, int head) {
            string p = PathFor(stepFolder, head);
            if (!File.Exists(p)) p = stepFolder + "led_select.txt";
            if (!File.Exists(p)) return new LedSelect();
            try { return Parse(File.ReadAllLines(p), p); }
            catch (Exception ex) { return new LedSelect { Error = ex.Message, Source = p }; }
        }

        public static LedSelect Parse(IEnumerable<string> lines, string source) {
            var s = new LedSelect { Source = source, All = false };
            bool sawOn = false;
            int lineNo = 0;
            foreach (string raw in lines) {
                lineNo++;
                string line = raw.Trim();
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash).Trim();
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq < 1) { s.Error = "line " + lineNo + ": expected  on=...  /  off=...  /  hold_ms=...  but found: " + line; return s; }
                string key = line.Substring(0, eq).Trim().ToLowerInvariant(), val = line.Substring(eq + 1).Trim();
                string err;
                if (key == "on") {
                    bool all; SortedSet<int> set;
                    if (!ParseList(val, out all, out set, out err)) { s.Error = "line " + lineNo + ": " + err; return s; }
                    sawOn = true; s.All = all; s.On = set;
                } else if (key == "off") {
                    bool all; SortedSet<int> set;
                    if (!ParseList(val, out all, out set, out err) || all) { s.Error = "line " + lineNo + ": " + (err ?? "off=all makes no sense"); return s; }
                    s.Off = set;
                } else if (key == "hold_ms") {
                    int v;
                    if (!int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 0 || v > 600000) { s.Error = "line " + lineNo + ": hold_ms must be 0 - 600000"; return s; }
                    s.HoldMs = v;
                } else { s.Error = "line " + lineNo + ": unknown key '" + key + "' (use on, off, hold_ms)"; return s; }
            }
            if (!sawOn) s.All = true;                                     // no on= line (empty file, or only off=4,8): every other taught LED must be on
            foreach (int n in s.Off) if (s.On.Contains(n)) { s.Error = "LED " + n + " is in both on= and off="; return s; }
            return s;
        }

        /// <summary>"1-3,5-7,9-10" / "1 4 7" / "all"  ->  set. Separators: comma, semicolon, space. LED numbers 1 - 100.</summary>
        public static bool ParseList(string text, out bool all, out SortedSet<int> set, out string error) {
            all = false; set = new SortedSet<int>(); error = null;
            string t = (text ?? "").Trim();
            if (t.Length == 0) return true;
            if (string.Equals(t, "all", StringComparison.OrdinalIgnoreCase)) { all = true; return true; }
            foreach (string tok0 in t.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)) {
                string tok = tok0.Trim();
                int dash = tok.IndexOf('-', tok.Length > 0 && tok[0] == '-' ? 1 : 0);
                int a, b;
                if (dash < 0) {
                    if (!int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out a)) { error = "'" + tok + "' is not a LED number"; return false; }
                    b = a;
                } else {
                    if (!int.TryParse(tok.Substring(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out a) ||
                        !int.TryParse(tok.Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out b)) { error = "'" + tok + "' is not a range like 3-7"; return false; }
                    if (b < a) { error = "range '" + tok + "' goes backwards"; return false; }
                }
                if (a < 1 || b > MaxLed) { error = "LED numbers must be 1 - " + MaxLed + " (found '" + tok + "')"; return false; }
                for (int n = a; n <= b; n++) set.Add(n);
            }
            return true;
        }

        /// <summary>{1,2,3,5,6,7,9,10} -> "1-3,5-7,9-10"</summary>
        public static string Format(IEnumerable<int> numbers) {
            var l = numbers.Distinct().OrderBy(x => x).ToList();
            if (l.Count == 0) return "";
            var sb = new StringBuilder();
            int i = 0;
            while (i < l.Count) {
                int j = i;
                while (j + 1 < l.Count && l[j + 1] == l[j] + 1) j++;
                if (sb.Length > 0) sb.Append(',');
                if (j - i >= 1) sb.Append(l[i]).Append('-').Append(l[j]);
                else for (int k = i; k <= j; k++) { if (k > i) sb.Append(','); sb.Append(l[k]); }
                i = j + 1;
            }
            return sb.ToString();
        }

        /// <summary>The text of the file for this selection (used by the menu).</summary>
        public string ToFileText() {
            var sb = new StringBuilder();
            sb.AppendLine("# Which LEDs of this Multi-LED mode are checked.  LED number = number of the ROI (LED 1 = Image1.png).");
            sb.AppendLine("# on=1-3,5-7,9-10   these LEDs must be lit, all together (LED 4 and 8 are not looked at)");
            sb.AppendLine("# on=all            every taught LED must be lit");
            sb.AppendLine("# off=4,8           (optional) these LEDs must be dark");
            sb.AppendLine("# hold_ms=500       (optional) how long they must stay like that");
            sb.AppendLine("on=" + (All ? "all" : Format(On)));
            if (Off.Count > 0) sb.AppendLine("off=" + Format(Off));
            if (HoldMs >= 0) sb.AppendLine("hold_ms=" + HoldMs);
            return sb.ToString();
        }

        /// <summary>The LED numbers that must be on, given how many LEDs are taught (last taught number).</summary>
        public List<int> OnList(int taughtCount) {
            if (All) { var l = new List<int>(); for (int n = 1; n <= taughtCount; n++) if (!Off.Contains(n)) l.Add(n); return l; }
            return On.ToList();
        }
    }
}
