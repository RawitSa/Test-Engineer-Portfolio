using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace camera_show {
    /// <summary>
    /// Real-time debug pictures. Written to  <config dir>\debug\  (overwritten about 2-3 times a second while the job runs):
    ///   frame_view.png          the real camera picture with the boxes turned onto the workpiece (as on the screen)
    ///   frame_flat.png          the real picture after the light is removed
    ///   frame_flat_contrast.png the same, contrast x3, so faint things are easy to see
    ///   frame_flat_upright.png  internal copy turned back to the taught position (what the symbol check compares)
    ///   S1.png, S2.png ...      per symbol: [taught symbol | live symbol | difference x4], the tiles drawn on the live one
    ///                           (green = tile ok, red = tile too low, number = tile score) and the scores S1 S2 C T in the header
    ///   scores.txt              the same scores as text
    /// debug\pass\ is written the moment a job passes, debug\fail\ is a copy of the last pictures when a job fails.
    /// </summary>
    public static class SymbolDebug {
        public static bool Enabled = true;

        private static bool _warned;

        /// <summary>Imwrite that tells camera_show_hub.log once when a picture could not be written (folder missing, read-only, non-ASCII path).</summary>
        private static void Write(string path, IInputArray img) {
            bool ok = false;
            try { ok = CvInvoke.Imwrite(path, img); } catch (Exception ex) { LogOnce("SYMBOL debug: write failed " + path + " : " + ex.Message); return; }
            if (!ok) LogOnce("SYMBOL debug: could not write " + path + " (is the folder writable? a non-ASCII path can fail in OpenCV)");
        }

        private static void LogOnce(string msg) {
            if (_warned) return;
            _warned = true;
            try { CameraHub.Log(msg); } catch { }
        }

        private static string Root() {
            string d = Path.Combine(AppPaths.ConfigDir, "debug");
            Directory.CreateDirectory(d);
            return d;
        }

        public static void Dump(Mat raw, Mat norm, List<SymbolTemplate> tpl, SymbolResult[] results, SymbolOptions[] os, Image<Bgr, byte> shown, string sub) {
            if (!Enabled) return;
            try {
                string dir = Root();
                if (sub != null) { dir = Path.Combine(dir, sub); Directory.CreateDirectory(dir); }

                if (shown != null) Write(Path.Combine(dir, "frame_view.png"), shown);
                // the real camera picture with the light removed (what the matcher "sees"), and the same with contrast x3
                using (Mat g = SymbolMatcher.Gray(raw))
                using (Mat f = SymbolMatcher.Flat(g, 25.0))
                using (Mat fc = new Mat()) {
                    Write(Path.Combine(dir, "frame_flat.png"), f);
                    f.ConvertTo(fc, DepthType.Cv8U, 3.0, -256.0);              // 128 + 3 x (v - 128)
                    Write(Path.Combine(dir, "frame_flat_contrast.png"), fc);
                }
                // the internal copy turned back to the taught position (only to see what the symbol check compares)
                using (Mat g2 = SymbolMatcher.Gray(norm))
                using (Mat f2 = SymbolMatcher.Flat(g2, 25.0))
                    Write(Path.Combine(dir, "frame_flat_upright.png"), f2);

                var sb = new StringBuilder();
                for (int i = 0; i < results.Length; i++) {
                    SymbolResult r = results[i];
                    SymbolOptions o = os[i];
                    if (r == null) { sb.AppendLine("S" + (i + 1) + "  n/a"); continue; }
                    sb.AppendLine("S" + (i + 1) + "  " + (r.Pass ? "PASS" : "FAIL") + "  S1=" + r.S1.ToString("F2") + " (min " + o.ZnccMin.ToString("F2") + ")"
                        + "  S2=" + r.S2.ToString("F2") + " (min " + o.EdgeMin.ToString("F2") + ")"
                        + "  C=" + r.C.ToString("F2") + " (min " + o.ContrastMin.ToString("F2") + ")"
                        + "  T=" + r.T.ToString("F2") + " (min " + o.TileMin.ToString("F2") + ")"
                        + "  G=" + r.G.ToString("F2") + " (min " + o.SegMin.ToString("F2") + ")  " + r.Note);
                    if (r.Live != null) {
                        using (Mat panel = Panel(tpl[i], r, o, i + 1))
                            Write(Path.Combine(dir, "S" + (i + 1) + ".png"), panel);
                    }
                }
                File.WriteAllText(Path.Combine(dir, "scores.txt"), sb.ToString());
            } catch (Exception ex) { LogOnce("SYMBOL debug: " + ex.Message); }
        }

        /// <summary>Copies the newest live pictures to debug\&lt;sub&gt;\ (used when a job fails).</summary>
        public static void Snapshot(string sub) {
            if (!Enabled) return;
            try {
                string root = Root(), dst = Path.Combine(root, sub);
                Directory.CreateDirectory(dst);
                foreach (string f in Directory.GetFiles(root))
                    File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            } catch { }
        }

        private static Mat Panel(SymbolTemplate t, SymbolResult r, SymbolOptions o, int n) {
            int sc = Math.Min(6, Math.Max(1, 140 / Math.Max(1, t.Size.Height)));
            int w = t.Size.Width * sc, h = t.Size.Height * sc, head = 40;
            var panel = new Mat(h + head, w * 3 + 8, DepthType.Cv8U, 3);
            panel.SetTo(new MCvScalar(40, 40, 40));

            using (Mat diff = new Mat())
            using (Mat diff4 = new Mat()) {
                CvInvoke.AbsDiff(t.Flat, r.Live, diff);
                diff.ConvertTo(diff4, DepthType.Cv8U, 4.0, 0.0);
                Put(panel, t.Flat, 0, head, w, h);
                Put(panel, r.Live, w + 4, head, w, h);
                Put(panel, diff4, 2 * w + 8, head, w, h);
            }

            // the tiles on the live symbol
            if (t.Tiles != null && r.TileScores != null) {
                for (int i = 0; i < t.Tiles.Length && i < r.TileScores.Length; i++) {
                    Rectangle tr = t.Tiles[i];
                    bool ok = r.TileScores[i] >= o.TileMin;
                    var col = ok ? new MCvScalar(0, 200, 0) : new MCvScalar(0, 0, 255);
                    var rc = new Rectangle(w + 4 + tr.X * sc, head + tr.Y * sc, tr.Width * sc, tr.Height * sc);
                    CvInvoke.Rectangle(panel, rc, col, 1);
                    CvInvoke.PutText(panel, r.TileScores[i].ToString("F2"), new Point(rc.X + 2, rc.Y + 12), FontFace.HersheySimplex, 0.4, col, 1);
                }
            }

            // the strokes (segments) on the difference picture: green = dark enough, red = too weak (with its value)
            if (r.SegScores != null) {
                for (int i = 0; i < t.Segs.Count && i < r.SegScores.Length; i++) {
                    Rectangle b = t.Segs[i].Box;
                    bool ok = r.SegScores[i] >= o.SegMin;
                    var col = ok ? new MCvScalar(0, 200, 0) : new MCvScalar(0, 0, 255);
                    var rc = new Rectangle(2 * w + 8 + b.X * sc, head + b.Y * sc, Math.Max(2, b.Width * sc), Math.Max(2, b.Height * sc));
                    CvInvoke.Rectangle(panel, rc, col, 1);
                    if (!ok) CvInvoke.PutText(panel, r.SegScores[i].ToString("F2"), new Point(rc.X + 1, rc.Y + 11), FontFace.HersheySimplex, 0.38, col, 1);
                }
            }

            var hc = r.Pass ? new MCvScalar(0, 220, 0) : new MCvScalar(0, 0, 255);
            CvInvoke.PutText(panel, "S" + n + " " + (r.Pass ? "PASS" : "FAIL") + "  S1=" + r.S1.ToString("F2") + " S2=" + r.S2.ToString("F2")
                + " C=" + r.C.ToString("F2") + " T=" + r.T.ToString("F2") + " G=" + r.G.ToString("F2"), new Point(4, 15), FontFace.HersheySimplex, 0.45, hc, 1);
            CvInvoke.PutText(panel, "taught | live | diff x4 + strokes", new Point(4, 33), FontFace.HersheySimplex, 0.4, new MCvScalar(200, 200, 200), 1);
            return panel;
        }

        private static void Put(Mat panel, Mat gray, int x, int y, int w, int h) {
            using (Mat big = new Mat())
            using (Mat bgr = new Mat()) {
                CvInvoke.Resize(gray, big, new Size(w, h), 0, 0, Inter.Nearest);
                CvInvoke.CvtColor(big, bgr, ColorConversion.Gray2Bgr);
                using (Mat roi = new Mat(panel, new Rectangle(x, y, w, h))) bgr.CopyTo(roi);
            }
        }
    }
}
