using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace camera_show {
    /// <summary>
    /// Settings of the per-symbol Compare check. config\camera_show_template.txt, lines "key=value", '#' = comment (all optional).
    ///   symbols=1          1 = every saved step is ONE symbol, all checked together (default). 0 = the old sequential check
    ///   sym_zncc=0.55      S1: normalised correlation of the symbol (light removed) must reach this
    ///   sym_edge=0.40      S2: correlation of the symbol's edges (shape) must reach this
    ///   sym_contrast=0.50  C : contrast of the live strokes against their surroundings, as a share of the template's contrast
    ///   hold_ms=800        ALL symbols must pass together this long (a time, not a frame count)
    ///   edge_shift=2       pixels the edge match may sit away from the position of the S1 match
    /// Calibrate the three limits from the "SYMBOL" lines of camera_show_hub.log (scores of good units, then set about 0.9 x the lowest).
    /// </summary>
    public sealed class SymbolOptions {
        public bool   Enabled    = true;
        public double ZnccMin    = 0.70;     // S1  (was 0.55: too loose, a part of a symbol could still pass)
        public double EdgeMin    = 0.50;     // S2
        public double ContrastMin = 0.60;    // C
        public double TileMin    = 0.45;     // T   every tile of the symbol must reach this
        public int    Grid       = 3;        //     the symbol is cut in Grid x Grid tiles
        public int    HoldMs     = 800;
        public int    EdgeShift  = 2;
        public double SegMin     = 0.50;     // G   every stroke (segment) of the symbol must be at least this share as dark as taught
        public bool   UseCalib   = true;     // use camera_show_calib.txt (thresholds learned per symbol) when it exists
        public bool   Learn      = false;    // sym_learn=1: learn the thresholds from a good unit and write camera_show_calib.txt
        public int    LearnFrames = 60;      // good frames to learn from
        public int    AiMode     = 1;        // ai=  0 off | 1 AI decides (default, classical only when no model) | 2 AI AND classical | 3 AI OR classical
        public double AiMin      = 0.90;     // ai_min=  the AI must be at least this sure that the symbol is complete (higher = fewer bad units pass)
        public double AiShift    = 0.06;     // ai_shift=  if the boxes are a little off (blur) try ONE common offset of this fraction of the symbol size; 0 = off
        public bool   AiCollect  = false;    // ai_collect=1  keep uncertain / disagreeing crops in config\ai_collect\ for re-training
        public bool   Debug      = true;     // save the light-removed pictures and a comparison per symbol to <config>\debug\

        public SymbolOptions Clone() { return (SymbolOptions)MemberwiseClone(); }

        public static SymbolOptions Load() {
            var o = new SymbolOptions();
            try {
                string f = AppPaths.ConfigDir + "camera_show_template.txt";
                if (!File.Exists(f)) return o;
                foreach (string raw in File.ReadAllLines(f)) {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 1) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                    double d;
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) continue;
                    switch (k) {
                        case "symbols":      o.Enabled     = d != 0; break;
                        case "sym_zncc":     o.ZnccMin     = Math.Max(-1.0, Math.Min(1.0, d)); break;
                        case "sym_edge":     o.EdgeMin     = Math.Max(-1.0, Math.Min(1.0, d)); break;
                        case "sym_contrast": o.ContrastMin = Math.Max(0.0, Math.Min(2.0, d)); break;
                        case "hold_ms":      o.HoldMs      = Math.Max(0, (int)d); break;
                        case "edge_shift":   o.EdgeShift   = Math.Max(0, Math.Min(10, (int)d)); break;
                        case "sym_tile":     o.TileMin     = Math.Max(-1.0, Math.Min(1.0, d)); break;
                        case "sym_grid":     o.Grid        = Math.Max(1, Math.Min(6, (int)d)); break;
                        case "sym_seg":      o.SegMin      = Math.Max(0.0, Math.Min(2.0, d)); break;
                        case "sym_calib":    o.UseCalib    = d != 0; break;
                        case "sym_learn":    o.Learn       = d != 0; break;
                        case "sym_learn_frames": o.LearnFrames = Math.Max(10, (int)d); break;
                        case "ai":           o.AiMode      = Math.Max(0, Math.Min(3, (int)d)); break;
                        case "ai_min":       o.AiMin       = Math.Max(0.5, Math.Min(0.999, d)); break;
                        case "ai_shift":     o.AiShift     = Math.Max(0.0, Math.Min(0.15, d)); break;
                        case "ai_collect":   o.AiCollect   = d != 0; break;
                        case "sym_debug":    o.Debug       = d != 0; break;
                    }
                }
            } catch { }
            return o;
        }
    }

    /// <summary>One stroke (segment) of a symbol: a connected piece of the taught stroke mask and the ring of background around it.</summary>
    public sealed class SymbolSeg : IDisposable {
        public Mat Mask, Ring;
        public Rectangle Box;
        public int Area;
        public double Contrast;          // taught: mean(ring) - mean(stroke), in flat-field units
        public void Dispose() { if (Mask != null) Mask.Dispose(); if (Ring != null) Ring.Dispose(); }
    }

    /// <summary>One taught symbol, prepared once per job: flat picture, edge picture, stroke mask, background mask.</summary>
    public sealed class SymbolTemplate : IDisposable {
        public Mat   Flat, Grad, Ink, Bg;
        public Size  Size;
        public double Sigma;
        public double Contrast;          // template: mean(background) - mean(strokes), in flat-field units
        public List<SymbolSeg> Segs = new List<SymbolSeg>();   // the strokes of the symbol, each one measured on its own
        public Rectangle[] Tiles;        // the tiles that have something in them (blank tiles are skipped)
        public bool   HasStrokes;        // false: not enough strokes to measure contrast (that check is then skipped)

        public static SymbolTemplate Build(Mat templateBgr) { return Build(templateBgr, 3); }

        public static SymbolTemplate Build(Mat templateBgr, int grid) {
            var t = new SymbolTemplate();
            t.Size = templateBgr.Size;
            t.Sigma = Math.Max(4.0, Math.Min(t.Size.Width, t.Size.Height) / 6.0);
            using (Mat g = SymbolMatcher.Gray(templateBgr)) t.Flat = SymbolMatcher.Flat(g, t.Sigma);
            t.Grad = SymbolMatcher.Gradient(t.Flat);

            t.Ink = new Mat();
            CvInvoke.Threshold(t.Flat, t.Ink, 0, 255, ThresholdType.BinaryInv | ThresholdType.Otsu);
            using (Mat k = CvInvoke.GetStructuringElement(ElementShape.Ellipse, new Size(5, 5), new Point(-1, -1)))
            using (Mat dil = new Mat()) {
                CvInvoke.Dilate(t.Ink, dil, k, new Point(-1, -1), 1, BorderType.Reflect101, new MCvScalar());
                t.Bg = new Mat();
                CvInvoke.BitwiseNot(dil, t.Bg);                              // background, kept clear of the stroke edges
            }
            int total = t.Size.Width * t.Size.Height;
            int ink = CvInvoke.CountNonZero(t.Ink), bg = CvInvoke.CountNonZero(t.Bg);
            t.HasStrokes = ink >= total * 0.02 && bg >= total * 0.02;
            if (t.HasStrokes)
                t.Contrast = CvInvoke.Mean(t.Flat, t.Bg).V0 - CvInvoke.Mean(t.Flat, t.Ink).V0;

            // segments: every connected stroke of the symbol on its own (a digit = its 7 segments, an icon = its parts)
            int minArea = Math.Max(10, total / 250);
            using (Mat labels = new Mat())
            using (Mat k9 = CvInvoke.GetStructuringElement(ElementShape.Ellipse, new Size(9, 9), new Point(-1, -1))) {
                int nl = CvInvoke.ConnectedComponents(t.Ink, labels, LineType.EightConnected, DepthType.Cv32S);
                for (int lb = 1; lb < nl && lb < 200; lb++) {
                    Mat m = new Mat();
                    CvInvoke.Compare(labels, new ScalarArray((double)lb), m, CmpType.Equal);
                    int area = CvInvoke.CountNonZero(m);
                    if (area < minArea) { m.Dispose(); continue; }
                    Mat ring = new Mat();
                    using (Mat d9 = new Mat()) {
                        CvInvoke.Dilate(m, d9, k9, new Point(-1, -1), 1, BorderType.Reflect101, new MCvScalar());
                        CvInvoke.BitwiseAnd(d9, t.Bg, ring);
                    }
                    double c = CvInvoke.Mean(t.Flat, ring).V0 - CvInvoke.Mean(t.Flat, m).V0;
                    if (CvInvoke.CountNonZero(ring) < 6 || c < 6.0) { m.Dispose(); ring.Dispose(); continue; }   // too faint to measure
                    t.Segs.Add(new SymbolSeg { Mask = m, Ring = ring, Box = CvInvoke.BoundingRectangle(m), Area = area, Contrast = c });
                }
            }
            t.Segs.Sort((a, b) => b.Area.CompareTo(a.Area));
            while (t.Segs.Count > 30) { t.Segs[t.Segs.Count - 1].Dispose(); t.Segs.RemoveAt(t.Segs.Count - 1); }

            // tiles: the symbol is checked part by part, so a part that is covered or missing cannot hide behind the parts that are fine
            var list = new List<Rectangle>();
            int gn = Math.Max(1, grid);
            for (int gy = 0; gy < gn; gy++)
                for (int gx = 0; gx < gn; gx++) {
                    int x0 = t.Size.Width * gx / gn,  x1 = t.Size.Width * (gx + 1) / gn;
                    int y0 = t.Size.Height * gy / gn, y1 = t.Size.Height * (gy + 1) / gn;
                    var rc = new Rectangle(x0, y0, x1 - x0, y1 - y0);
                    if (rc.Width < 6 || rc.Height < 6) continue;
                    using (Mat tile = new Mat(t.Flat, rc)) {
                        MCvScalar m = new MCvScalar(), sd = new MCvScalar();
                        CvInvoke.MeanStdDev(tile, ref m, ref sd);
                        if (sd.V0 >= 4.0) list.Add(rc);            // blank tile: nothing to check there
                    }
                }
            t.Tiles = list.ToArray();
            return t;
        }

        public void Dispose() {
            if (Flat != null) Flat.Dispose(); if (Grad != null) Grad.Dispose();
            if (Ink != null) Ink.Dispose();   if (Bg != null) Bg.Dispose();
            foreach (SymbolSeg sg in Segs) sg.Dispose();
        }
    }

    public sealed class SymbolResult : IDisposable {
        public bool      Pass;
        public double    S1 = -1, S2 = -1, C;
        public Rectangle Match;                  // inside the search area
        public string    Note = string.Empty;
        public double    T = 1;                  // worst tile
        public double    G = 1;                  // worst stroke (segment)
        public double    Ai = -1;                // AI: probability that the symbol is complete (-1 = AI not used)
        public double[]  SegScores;              // contrast of every stroke, as a share of the taught one (same order as SymbolTemplate.Segs)
        public double[]  TileScores;             // score of every tile (same order as SymbolTemplate.Tiles)
        public Mat       Live;                   // light-removed picture of the symbol as seen now (only when debug is on)
        public void Dispose() { if (Live != null) { Live.Dispose(); Live = null; } }
    }

    /// <summary>
    /// "Is this symbol really there, and is it THIS symbol?" for one taught symbol, without any black / white threshold at run time:
    ///   S1  correlation of the light-corrected picture with the taught symbol, searched only in a small window
    ///   S2  correlation of the edge pictures, so the SHAPE must match (a half drawn or ghost symbol scores low)
    ///   C   contrast of the strokes against their surroundings, relative to the taught symbol (noise / glare has none)
    /// Pass = S1 and S2 and C all reach their limits.
    /// </summary>
    public static class SymbolMatcher {

        public static SymbolResult Check(SymbolTemplate t, Mat regionBgr, SymbolOptions o) {
            var r = new SymbolResult();
            int tw = t.Size.Width, th = t.Size.Height;
            if (regionBgr.Width < tw || regionBgr.Height < th) { r.Note = "search area smaller than symbol"; return r; }

            using (Mat gs = Gray(regionBgr))
            using (Mat fs = Flat(gs, t.Sigma))
            using (Mat res = new Mat()) {
                CvInvoke.MatchTemplate(fs, t.Flat, res, TemplateMatchingType.CcoeffNormed);
                double mn = 0, mx = 0; Point pmn = new Point(), pmx = new Point();
                CvInvoke.MinMaxLoc(res, ref mn, ref mx, ref pmn, ref pmx);
                if (double.IsNaN(mx) || double.IsInfinity(mx)) mx = -1;
                r.S1 = mx;
                r.Match = new Rectangle(pmx, t.Size);

                // S2: shape. The best edge match must sit within edge_shift pixels of the S1 position.
                using (Mat gr = Gradient(fs))
                using (Mat res2 = new Mat()) {
                    CvInvoke.MatchTemplate(gr, t.Grad, res2, TemplateMatchingType.CcoeffNormed);
                    var win = Rectangle.Intersect(
                        new Rectangle(pmx.X - o.EdgeShift, pmx.Y - o.EdgeShift, 2 * o.EdgeShift + 1, 2 * o.EdgeShift + 1),
                        new Rectangle(Point.Empty, res2.Size));
                    if (win.Width > 0 && win.Height > 0) {
                        using (Mat sub = new Mat(res2, win)) {
                            double mn2 = 0, mx2 = 0; Point a = new Point(), b = new Point();
                            CvInvoke.MinMaxLoc(sub, ref mn2, ref mx2, ref a, ref b);
                            r.S2 = (double.IsNaN(mx2) || double.IsInfinity(mx2)) ? -1 : mx2;
                        }
                    }
                }

                // C: strokes against surroundings, at the matched place, relative to the taught symbol
                if (t.HasStrokes && t.Contrast > 1.0) {
                    using (Mat obs = new Mat(fs, r.Match)) {
                        double live = CvInvoke.Mean(obs, t.Bg).V0 - CvInvoke.Mean(obs, t.Ink).V0;
                        r.C = live / t.Contrast;
                    }
                } else r.C = 1.0;                                          // nothing to measure: this check does not decide

                // T: every tile of the symbol must be there. A wire / hand over ONE part of the symbol drops this value
                // (a tile may sit 1 px away from its place, so a tiny shift does not matter)
                r.T = 1.0;
                if (t.Tiles != null && t.Tiles.Length > 0) {
                    r.TileScores = new double[t.Tiles.Length];
                    Rectangle fb = new Rectangle(Point.Empty, fs.Size);
                    using (Mat one = new Mat()) {
                        for (int ti = 0; ti < t.Tiles.Length; ti++) {
                            Rectangle tr = t.Tiles[ti];
                            Rectangle big = Rectangle.Intersect(new Rectangle(r.Match.X + tr.X - 1, r.Match.Y + tr.Y - 1, tr.Width + 2, tr.Height + 2), fb);
                            double ts = -1;
                            if (big.Width >= tr.Width && big.Height >= tr.Height) {
                                using (Mat ot = new Mat(fs, big))
                                using (Mat tt = new Mat(t.Flat, tr)) {
                                    CvInvoke.MatchTemplate(ot, tt, one, TemplateMatchingType.CcoeffNormed);
                                    double ta = 0, tb = 0; Point pa = new Point(), pb = new Point();
                                    CvInvoke.MinMaxLoc(one, ref ta, ref tb, ref pa, ref pb);
                                    ts = (double.IsNaN(tb) || double.IsInfinity(tb)) ? -1 : tb;   // flat where strokes should be = missing
                                }
                            }
                            r.TileScores[ti] = ts;
                            if (ts < r.T) r.T = ts;
                        }
                    }
                }

                // G: every stroke (segment) on its own: is it as dark against ITS surroundings as it was taught?
                // (looked for at the matched place and 1 px around it, a stroke is only 3-5 px wide)
                r.G = 1.0;
                if (t.Segs.Count > 0) {
                    r.SegScores = new double[t.Segs.Count];
                    Rectangle fb2 = new Rectangle(Point.Empty, fs.Size);
                    for (int sk = 0; sk < t.Segs.Count; sk++) {
                        SymbolSeg sg = t.Segs[sk];
                        double best = -1;
                        for (int sy = -1; sy <= 1; sy++)
                            for (int sx = -1; sx <= 1; sx++) {
                                Rectangle at = new Rectangle(r.Match.X + sx, r.Match.Y + sy, t.Size.Width, t.Size.Height);
                                if (!fb2.Contains(at)) continue;
                                using (Mat po = new Mat(fs, at)) {
                                    double stroke = CvInvoke.Mean(po, sg.Mask).V0, around = CvInvoke.Mean(po, sg.Ring).V0;
                                    double ratio = (around - stroke) / sg.Contrast;
                                    if (ratio > best) best = ratio;
                                }
                            }
                        r.SegScores[sk] = best;
                        if (best < r.G) r.G = best;
                    }
                }

                if (SymbolDebug.Enabled) {
                    using (Mat lv = new Mat(fs, r.Match)) { r.Live = new Mat(); lv.CopyTo(r.Live); }
                }
            }

            r.Pass = r.S1 >= o.ZnccMin && r.S2 >= o.EdgeMin && r.C >= o.ContrastMin && r.T >= o.TileMin && r.G >= o.SegMin;
            if (!r.Pass) {
                string why = string.Empty;
                if (r.S1 < o.ZnccMin)    why += "S1 ";
                if (r.S2 < o.EdgeMin)    why += "S2 ";
                if (r.C  < o.ContrastMin) why += "C ";
                if (r.T  < o.TileMin)    why += "T ";
                if (r.G  < o.SegMin)     why += "G ";
                r.Note = "low " + why.Trim();
            }
            return r;
        }

        internal static Mat Gray(Mat bgr) {
            var g = new Mat();
            if (bgr.NumberOfChannels == 1) bgr.CopyTo(g);
            else CvInvoke.CvtColor(bgr, g, ColorConversion.Bgr2Gray);
            CvInvoke.GaussianBlur(g, g, new Size(3, 3), 0);
            return g;
        }

        /// <summary>gray / blurred gray: ~128 on an even surface, lower on strokes, whatever the light.</summary>
        internal static Mat Flat(Mat gray, double sigma) {
            using (Mat blur = new Mat())
            {
                CvInvoke.GaussianBlur(gray, blur, new Size(0, 0), sigma);
                var flat = new Mat();
                CvInvoke.Divide(gray, blur, flat, 128.0, DepthType.Cv8U);
                return flat;
            }
        }

        /// <summary>Edge strength (Sobel magnitude), 32-bit float. (sqrt(dx*dx + dy*dy); CvInvoke.Magnitude is missing in some Emgu versions)</summary>
        internal static Mat Gradient(Mat flat) {
            using (Mat dx = new Mat())
            using (Mat dy = new Mat())
            using (Mat dx2 = new Mat())
            using (Mat dy2 = new Mat())
            using (Mat sum = new Mat()) {
                CvInvoke.Sobel(flat, dx, DepthType.Cv32F, 1, 0, 3);
                CvInvoke.Sobel(flat, dy, DepthType.Cv32F, 0, 1, 3);
                CvInvoke.Multiply(dx, dx, dx2);
                CvInvoke.Multiply(dy, dy, dy2);
                CvInvoke.Add(dx2, dy2, sum);
                var mag = new Mat();
                CvInvoke.Sqrt(sum, mag);
                return mag;
            }
        }
    }
}
