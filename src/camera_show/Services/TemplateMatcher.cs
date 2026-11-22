using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace camera_show {
    /// <summary>
    /// Settings of the Compare (template) check. All optional: config\camera_show_template.txt, lines "key=value", '#' = comment.
    ///   tolerance=3        pixels a stroke may be off the template (camera / part moves a little)
    ///   cell_min=0.35      share of a template cell's ink that must be found again (lower = more forgiving)
    ///   cell_ink=0.45      only cells with at least this share of ink in the template are checked (skips dust, background)
    ///   cell_fail_max=0    cells allowed to fail. 0 = every digit / segment / icon must be present
    ///   min_score=0.30     the pattern must be found at all (normalised correlation)
    ///   stable_frames=3    consecutive good frames needed (a single lucky frame can not pass)
    ///   rotate=0           180 = the template was saved from a picture turned 180 degrees (old CheckOCR style)
    ///   cell_px=0          cell size in pixels, 0 = automatic
    /// </summary>
    public sealed class TemplateOptions {
        public int    Tolerance    = 3;
        public double CellMin      = 0.35;
        public double CellInk      = 0.45;
        public int    CellFailMax  = 0;
        public double MinScore     = 0.30;
        public int    StableFrames = 3;
        public int    Rotate       = 0;
        public int    CellPx       = 0;

        public static TemplateOptions Load() {
            var o = new TemplateOptions();
            try {
                string f = AppPaths.ConfigDir + "camera_show_template.txt";
                if (!File.Exists(f)) return o;
                foreach (string raw in File.ReadAllLines(f)) {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                    double d; int n;
                    bool isD = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
                    bool isN = int.TryParse(v, out n);
                    n = isN ? n : 0;
                    if (!isD) continue;
                    switch (k) {
                        case "tolerance":     o.Tolerance    = Math.Max(0, Math.Min(10, n)); break;
                        case "cell_min":      o.CellMin      = Math.Max(0.05, Math.Min(1.0, d)); break;
                        case "cell_ink":      o.CellInk      = Math.Max(0.05, Math.Min(1.0, d)); break;
                        case "cell_fail_max": o.CellFailMax  = Math.Max(0, n); break;
                        case "min_score":     o.MinScore     = Math.Max(-1.0, Math.Min(1.0, d)); break;
                        case "stable_frames": o.StableFrames = Math.Max(1, n); break;
                        case "rotate":        o.Rotate       = n == 180 ? 180 : 0; break;
                        case "cell_px":       o.CellPx       = Math.Max(0, n); break;
                    }
                }
            } catch { }
            return o;
        }
    }

    public sealed class TemplateResult {
        public bool            Pass;
        public double          Score;                       // how well the pattern was found (-1 = not at all)
        public Rectangle       Match;                       // where, inside the search area
        public int             InkCells, FailedCells;       // cells checked / cells whose ink is missing
        public List<Rectangle> Failed = new List<Rectangle>();   // those cells, in the coordinates of the TEMPLATE
        public string          Note = string.Empty;
    }

    /// <summary>
    /// Finds a template picture in a search area and checks that EVERYTHING in it is present, whatever the light is.
    ///   1. Light removal: every picture is divided by its own blurred copy (flat field), so a brighter / darker / washed-out
    ///      picture, a glare or a light gradient from the fixture no longer matters.
    ///   2. Position: normalised correlation of the two flattened pictures (also gain / offset free).
    ///   3. Ink: dark strokes are separated from the LCD background with Otsu on EACH picture, so the contrast of the
    ///      picture does not matter either.
    ///   4. Every cell of the template that holds ink must find that ink again (a few pixels of tolerance). One missing
    ///      digit, segment, dot or icon fails its cells -> FAIL. It does not average over the whole picture.
    /// </summary>
    public static class TemplateMatcher {

        public static TemplateResult Compare(Mat templateBgr, Mat regionBgr, TemplateOptions o) {
            var r = new TemplateResult { Score = -1 };
            int tw = templateBgr.Width, th = templateBgr.Height;
            if (tw < 12 || th < 12) { r.Note = "template picture is too small"; return r; }
            if (regionBgr.Width < tw || regionBgr.Height < th) { r.Note = "search area is smaller than the template"; return r; }

            double sigma = Math.Max(4.0, Math.Min(tw, th) / 6.0);
            using (Mat gt = Gray(templateBgr))
            using (Mat gs = Gray(regionBgr))
            using (Mat ft = Flat(gt, sigma))
            using (Mat fs = Flat(gs, sigma))
            using (Mat res = new Mat()) {
                CvInvoke.MatchTemplate(fs, ft, res, TemplateMatchingType.CcoeffNormed);
                double mn = 0, mx = 0; Point pmn = new Point(), pmx = new Point();
                CvInvoke.MinMaxLoc(res, ref mn, ref mx, ref pmn, ref pmx);
                if (double.IsNaN(mx) || double.IsInfinity(mx)) mx = -1;
                r.Score = mx;
                r.Match = new Rectangle(pmx, new Size(tw, th));
                if (mx < o.MinScore) { r.Note = "pattern not found"; return r; }

                using (Mat obs = new Mat(fs, r.Match))
                using (Mat inkT = Ink(ft))
                using (Mat inkO = Ink(obs))
                using (Mat dil = new Mat())
                using (Mat cover = new Mat())
                using (Mat kernel = CvInvoke.GetStructuringElement(ElementShape.Ellipse, new Size(2 * o.Tolerance + 1, 2 * o.Tolerance + 1), new Point(-1, -1))) {
                    CvInvoke.Dilate(inkO, dil, kernel, new Point(-1, -1), 1, BorderType.Reflect101, new MCvScalar());
                    CvInvoke.BitwiseAnd(inkT, dil, cover);

                    int c = o.CellPx > 0 ? o.CellPx : Math.Max(5, Math.Min(14, Math.Min(tw, th) / 18));
                    for (int y = 0; y < th; y += c) {
                        for (int x = 0; x < tw; x += c) {
                            int w = Math.Min(c, tw - x), h = Math.Min(c, th - y);
                            if (w * h < 9) continue;
                            var cell = new Rectangle(x, y, w, h);
                            int ti;
                            using (Mat sub = new Mat(inkT, cell)) ti = CvInvoke.CountNonZero(sub);
                            if (ti < o.CellInk * w * h) continue;                       // background / dust: not part of the pattern
                            r.InkCells++;
                            int both;
                            using (Mat sub = new Mat(cover, cell)) both = CvInvoke.CountNonZero(sub);
                            if (both < o.CellMin * ti) { r.FailedCells++; r.Failed.Add(cell); }
                        }
                    }
                }
            }
            r.Pass = r.FailedCells <= o.CellFailMax;
            r.Note = r.Pass ? "all present" : r.FailedCells + " of " + r.InkCells + " parts missing";
            return r;
        }

        private static Mat Gray(Mat bgr) {
            var g = new Mat();
            if (bgr.NumberOfChannels == 1) bgr.CopyTo(g);
            else CvInvoke.CvtColor(bgr, g, ColorConversion.Bgr2Gray);
            CvInvoke.GaussianBlur(g, g, new Size(3, 3), 0);
            return g;
        }

        /// <summary>gray / blurred gray: ~128 on an even surface, lower on strokes, whatever the light.</summary>
        private static Mat Flat(Mat gray, double sigma) {
            using (Mat blur = new Mat()) {
                CvInvoke.GaussianBlur(gray, blur, new Size(0, 0), sigma);
                var flat = new Mat();
                CvInvoke.Divide(gray, blur, flat, 128.0, DepthType.Cv8U);
                return flat;
            }
        }

        /// <summary>Dark strokes = 255. Otsu picks the split from this picture itself, so the contrast does not matter.</summary>
        private static Mat Ink(Mat flat) {
            var ink = new Mat();
            CvInvoke.Threshold(flat, ink, 0, 255, ThresholdType.BinaryInv | ThresholdType.Otsu);
            return ink;
        }
    }
}
