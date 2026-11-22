using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Emgu.CV;
using Emgu.CV.CvEnum;

namespace camera_show {
    /// <summary>What the AI says about ONE symbol in ONE frame.</summary>
    public sealed class AiResult {
        public bool   Valid;        // false = the crop could not be made (symbol outside the picture)
        public double Score;        // probability that THIS symbol is complete and clearly visible (0..1)
        public int    Best;         // class the AI thinks it sees: 0..20 = symbol S1..S21, 21 = NG (nothing / broken / hidden / wrong place)
        public double BestP;
        public string BestName = "";
        public byte[] Raw;          // the 64x64 grey crop the AI looked at (used to collect pictures for re-training)
    }

    /// <summary>
    /// AI check of the Compare symbols. The CNN is trained in Model_SST.ipynb on pictures of a symbol window
    /// (symbol box + 12 % on every side, grey, 64x64) and answers: "is this symbol there, complete, and is it THIS symbol?"
    /// Light (glare, haze, dark / bright, gradients), blur and noise are part of the training, so the answer does not depend on a
    /// fixed black / white level or on correlation with one taught picture. Where the window is comes from the classical tracker
    /// (SymbolTracker), which still follows the workpiece.
    ///
    /// Files in the config folder (written by the notebook):
    ///   symbol_ai.bin   weights        symbol_ai.txt   settings (img, margin, which classes count as "this symbol")
    /// Settings in camera_show_template.txt:  ai=1|2|3|0, ai_min=0.80, ai_collect=0|1   (see SymbolOptions)
    /// </summary>
    public sealed class SymbolAI {
        public const string BinFile  = "symbol_ai.bin";
        public const string MetaFile = "symbol_ai.txt";

        private SymbolNet _net;
        private int       _img;
        private double    _margin, _stdFloor;
        private int       _ngIndex;
        private int[][]   _accept;                 // per symbol: classes (0-based) that count as "this symbol"

        public string Info = "";                   // one line for the log
        public int    Symbols { get { return _accept == null ? 0 : _accept.Length; } }

        /// <summary>Loads the model from the config folder. Returns null (and why) when AI cannot be used - the check then stays classical.</summary>
        public static SymbolAI Load(int symbolCount, out string why) {
            why = null;
            string bin = AppPaths.ConfigDir + BinFile, meta = AppPaths.ConfigDir + MetaFile;
            if (!File.Exists(bin) || !File.Exists(meta)) { why = "no " + BinFile + " / " + MetaFile + " in " + AppPaths.ConfigDir; return null; }

            var ai = new SymbolAI();
            string err;
            ai._net = SymbolNet.Load(bin, out err);
            if (ai._net == null) { why = BinFile + ": " + err; return null; }

            ai._img = ai._net.Img; ai._margin = 0.12; ai._stdFloor = 8.0; ai._ngIndex = ai._net.Classes - 1;
            var accept = new Dictionary<int, int[]>();
            try {
                foreach (string raw in File.ReadAllLines(meta)) {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                    double d;
                    if (k == "img" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) { if ((int)d != ai._img) { why = "settings say img=" + v + " but the weights use " + ai._img; return null; } }
                    else if (k == "margin"    && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) ai._margin   = Math.Max(0.0, Math.Min(0.5, d));
                    else if (k == "std_floor" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) ai._stdFloor = Math.Max(1.0, d);
                    else if (k == "ng_index"  && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) ai._ngIndex  = (int)d;
                    else if (k.StartsWith("accept_")) {
                        int n;
                        if (!int.TryParse(k.Substring(7), out n) || n < 1) continue;
                        var list = new List<int>();
                        foreach (string part in v.Split(',')) { int c; if (int.TryParse(part.Trim(), out c) && c >= 1 && c <= ai._net.Classes) list.Add(c - 1); }
                        if (list.Count > 0) accept[n - 1] = list.ToArray();
                    }
                }
            } catch (Exception ex) { why = MetaFile + ": " + ex.Message; return null; }

            int count = 0;
            while (accept.ContainsKey(count)) count++;
            if (count == 0) { why = MetaFile + " has no accept_N lines"; return null; }
            if (count != symbolCount) { why = "the model knows " + count + " symbols but this step has " + symbolCount + " (teach 21 symbols, or train a model for this step)"; return null; }
            ai._accept = new int[count][];
            for (int i = 0; i < count; i++) ai._accept[i] = accept[i];

            ai.Info = "model " + ai._img + "x" + ai._img + ", " + ai._net.Classes + " classes, " + count + " symbols, margin " + ai._margin.ToString("F2", CultureInfo.InvariantCulture);
            return ai;
        }

        /// <summary>
        /// Asks the AI about every symbol at the place where the tracker expects it (norm = the picture turned back to the taught position).
        /// Step 1: every symbol at the expected place. If all are sure (>= minScore) that is the answer.
        /// Step 2 (only when something is not sure, and shiftFrac &gt; 0): the tracker may be a few pixels off (blur). ONE common
        /// offset for the whole LCD is tried (8 directions, shiftFrac of each symbol's size) and the offset that makes most symbols
        /// sure is chosen. A symbol that was not sure may take its score from that offset only if it reaches max(minScore, 0.95).
        /// One common offset (not a free search per symbol) keeps a broken symbol from "finding" a position where the rest of it looks right.
        /// </summary>
        public AiResult[] Evaluate(Mat norm, Rectangle[] expected, double minScore, double shiftFrac) {
            int n = Math.Min(expected.Length, _accept.Length);
            AiResult[] res = RunAll(norm, expected, n, 0.0, 0.0);
            bool allSure = true;
            for (int i = 0; i < n; i++) if (!res[i].Valid || res[i].Score < minScore) allSure = false;
            if (allSure || shiftFrac <= 0.0) return Pad(res, expected.Length);

            double sureAt = Math.Max(minScore, 0.95);
            double[][] dirs = { new[] { -1.0, -1.0 }, new[] { 0.0, -1.0 }, new[] { 1.0, -1.0 }, new[] { -1.0, 0.0 },
                                new[] { 1.0, 0.0 }, new[] { -1.0, 1.0 }, new[] { 0.0, 1.0 }, new[] { 1.0, 1.0 } };
            AiResult[] bestRes = null; int bestCount = -1; double bestSum = -1;
            foreach (double[] d in dirs) {
                AiResult[] cand = RunAll(norm, expected, n, d[0] * shiftFrac, d[1] * shiftFrac);
                int cnt = 0; double sum = 0;
                for (int i = 0; i < n; i++) if (cand[i].Valid) { sum += cand[i].Score; if (cand[i].Score >= sureAt) cnt++; }
                if (cnt > bestCount || (cnt == bestCount && sum > bestSum)) { bestCount = cnt; bestSum = sum; bestRes = cand; }
            }
            if (bestRes != null) {
                for (int i = 0; i < n; i++) {
                    if ((!res[i].Valid || res[i].Score < minScore) && bestRes[i].Valid && bestRes[i].Score >= sureAt && bestRes[i].Score > res[i].Score)
                        res[i] = bestRes[i];
                }
            }
            return Pad(res, expected.Length);
        }

        private static AiResult[] Pad(AiResult[] res, int total) {
            if (res.Length >= total) return res;
            var all = new AiResult[total];
            for (int i = 0; i < total; i++) all[i] = i < res.Length ? res[i] : new AiResult();
            return all;
        }

        /// <summary>All n symbols, window moved by (fx * width, fy * height). Crops one after the other (OpenCV), network in parallel.</summary>
        private AiResult[] RunAll(Mat norm, Rectangle[] expected, int n, double fx, double fy) {
            var res = new AiResult[n];
            var inputs = new float[n][];
            for (int i = 0; i < n; i++) {
                res[i] = new AiResult();
                try {
                    Rectangle r = expected[i];
                    if (fx != 0.0 || fy != 0.0) r.Offset((int)Math.Round(fx * r.Width), (int)Math.Round(fy * r.Height));
                    inputs[i] = Prepare(norm, r, res[i]);
                } catch (Exception ex) { CameraHub.Log("SYMBOL AI crop S" + (i + 1) + " failed: " + ex.Message); inputs[i] = null; }
            }
            Parallel.For(0, n, i => {
                if (inputs[i] == null) return;
                try {
                    float[] p = _net.Forward(inputs[i]);
                    double s = 0;
                    foreach (int c in _accept[i]) s += p[c];
                    int best = 0;
                    for (int c = 1; c < p.Length; c++) if (p[c] > p[best]) best = c;
                    res[i].Score = Math.Min(1.0, s);
                    res[i].Best = best; res[i].BestP = p[best];
                    res[i].BestName = best == _ngIndex ? "NG" : "S" + (best + 1);
                    res[i].Valid = true;
                } catch (Exception ex) { CameraHub.Log("SYMBOL AI network S" + (i + 1) + " failed: " + ex.Message); }
            });
            return res;
        }

        /// <summary>Cuts the window (box + margin) out of the picture, grey, img x img, standardised: (v - mean) / max(std, std_floor).</summary>
        private float[] Prepare(Mat norm, Rectangle rect, AiResult r) {
            int mx = (int)Math.Round(rect.Width * _margin), my = (int)Math.Round(rect.Height * _margin);
            Rectangle win = Rectangle.Inflate(rect, mx, my);
            Rectangle inter = Rectangle.Intersect(win, new Rectangle(Point.Empty, norm.Size));
            if (inter.Width < 8 || inter.Height < 8) return null;

            using (Mat piece = new Mat(norm, inter))
            using (Mat padded = new Mat())
            using (Mat gray = new Mat())
            using (Mat small = new Mat()) {
                Mat src = piece;
                if (inter != win) {                                  // symbol at the edge of the picture: repeat the edge pixels
                    CvInvoke.CopyMakeBorder(piece, padded, inter.Y - win.Y, win.Bottom - inter.Bottom, inter.X - win.X, win.Right - inter.Right, BorderType.Replicate);
                    src = padded;
                }
                if (src.NumberOfChannels == 1) src.CopyTo(gray); else CvInvoke.CvtColor(src, gray, ColorConversion.Bgr2Gray);
                CvInvoke.Resize(gray, small, new Size(_img, _img), 0, 0, Inter.Area);

                var raw = new byte[_img * _img];
                Marshal.Copy(small.DataPointer, raw, 0, raw.Length);
                r.Raw = raw;

                double sum = 0, sum2 = 0;
                for (int i = 0; i < raw.Length; i++) { sum += raw[i]; sum2 += (double)raw[i] * raw[i]; }
                double mean = sum / raw.Length;
                double std = Math.Sqrt(Math.Max(0.0, sum2 / raw.Length - mean * mean));
                double den = Math.Max(std, _stdFloor);
                var x = new float[raw.Length];
                for (int i = 0; i < raw.Length; i++) x[i] = (float)((raw[i] - mean) / den);
                return x;
            }
        }

        // ------------------------------------------------------------------------------------------------ collecting pictures
        private static readonly long[] LastSave = new long[64];
        private static int _saved = -1;

        /// <summary>
        /// ai_collect=1: keeps the crops the AI was NOT sure about, or where it disagreed with the classical check, in config\ai_collect\
        /// (file name: S07_ai0.62_disagree_....png). Sort them by hand into dataset\train\S07 or dataset\train\NG, re-train, copy the new
        /// symbol_ai.bin: the model then learns from the real machine. At most one picture per symbol per 1.5 s, 3000 files in all.
        /// </summary>
        public static void Collect(AiResult a, int symbol, string tag) {
            try {
                if (a == null || a.Raw == null || symbol < 0 || symbol >= LastSave.Length) return;
                long now = Environment.TickCount;
                if (now - LastSave[symbol] < 1500) return;
                string dir = AppPaths.ConfigDir + "ai_collect/";
                Directory.CreateDirectory(dir);
                if (_saved < 0) _saved = Directory.GetFiles(dir, "*.png").Length;
                if (_saved >= 3000) return;
                LastSave[symbol] = now;
                int side = (int)Math.Round(Math.Sqrt(a.Raw.Length));
                using (Mat m = new Mat(side, side, DepthType.Cv8U, 1)) {
                    Marshal.Copy(a.Raw, 0, m.DataPointer, a.Raw.Length);
                    string name = "S" + (symbol + 1).ToString("00") + "_ai" + a.Score.ToString("0.00", CultureInfo.InvariantCulture) + "_" + tag + "_" + DateTime.Now.ToString("yyMMdd_HHmmss_fff") + ".png";
                    if (CvInvoke.Imwrite(dir + name, m)) _saved++;
                }
            } catch { }
        }
    }
}
