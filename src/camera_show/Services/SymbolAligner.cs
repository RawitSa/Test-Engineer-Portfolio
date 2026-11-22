using System;
using System.Collections.Generic;
using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace camera_show {
    /// <summary>
    /// Keeps the movement of the workpiece (any turn, 0-360 degrees, plus shift) as ONE 2x3 matrix N that carries the live picture back
    /// to the taught picture:   x_taught = N0*x + N1*y + N2 ,   y_taught = N3*x + N4*y + N5.
    /// Normalize(frame) gives the live picture turned back upright, so the taught rectangles, the tracker and the symbol check work
    /// on it unchanged. Update(pose) follows the small movements from frame to frame; FindPose does the big jump (turned / moved far).
    /// </summary>
    /// <summary>The newest "live picture -> taught picture" matrix of each head, so that a symbol drawn again on the live picture is saved in TAUGHT coordinates.</summary>
    public static class SymbolPoseBus {
        private static readonly object Lock = new object();
        private static readonly Dictionary<int, double[]> Last = new Dictionary<int, double[]>();
        private static readonly Dictionary<int, int> Stamp = new Dictionary<int, int>();

        public static void Set(int head, double[] n) {
            lock (Lock) { if (n == null) { Last.Remove(head); Stamp.Remove(head); } else { Last[head] = (double[])n.Clone(); Stamp[head] = Environment.TickCount; } }
        }

        /// <summary>False when the tracker has not followed the LCD in the last 2 seconds (then nothing is known about where the workpiece is).</summary>
        public static bool TryGet(int head, out double[] n) {
            lock (Lock) {
                n = null;
                double[] v; int t;
                if (!Last.TryGetValue(head, out v) || !Stamp.TryGetValue(head, out t)) return false;
                if (Math.Abs(Environment.TickCount - t) > 2000) return false;
                n = (double[])v.Clone();
                return true;
            }
        }
    }

    public sealed class SymbolAligner {
        public double[] N = new double[] { 1, 0, 0, 0, 1, 0 };

        public void Reset() { N = new double[] { 1, 0, 0, 0, 1, 0 }; }

        /// <summary>The zoom the LCD was found at last time (live picture x this = taught size). Tried first when the LCD is lost, so after a lens change the search finds it again by itself.</summary>
        public static double LastScale = 1.0;

        private static readonly double[] ScaleGrid = { 0.94, 1.06, 0.89, 1.12, 0.84, 1.19, 0.79, 1.26, 0.74, 1.34, 0.70, 1.42, 0.66, 1.5 };

        /// <summary>Zoom factors to try when the LCD is lost: the last good one, 1.0, then 6 % steps both ways (live picture is resized by this factor, then searched).</summary>
        public static double[] ScaleOrder() {
            var l = new List<double>();
            l.Add(LastScale);
            if (Math.Abs(LastScale - 1.0) > 0.01) l.Add(1.0);
            foreach (double g in ScaleGrid) if (Math.Abs(g - LastScale) > 0.01) l.Add(g);
            return l.ToArray();
        }

        /// <summary>True when N does (almost) nothing: the live picture already is the taught picture.</summary>
        public static bool IsIdentity(double[] n) {
            return n == null || (Math.Abs(n[0] - 1) < 0.002 && Math.Abs(n[4] - 1) < 0.002 && Math.Abs(n[1]) < 0.002 && Math.Abs(n[3]) < 0.002 && Math.Abs(n[2]) < 0.5 && Math.Abs(n[5]) < 0.5);
        }

        /// <summary>Box of a rectangle of the LIVE picture, in the TAUGHT picture (bounding box of the 4 moved corners).</summary>
        public static Rectangle MapRect(double[] n, Rectangle r) {
            double[,] c = { { r.X, r.Y }, { r.Right, r.Y }, { r.Right, r.Bottom }, { r.X, r.Bottom } };
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int k = 0; k < 4; k++) {
                double x = n[0] * c[k, 0] + n[1] * c[k, 1] + n[2], y = n[3] * c[k, 0] + n[4] * c[k, 1] + n[5];
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
            return new Rectangle((int)Math.Round(x0), (int)Math.Round(y0), (int)Math.Round(x1 - x0), (int)Math.Round(y1 - y0));
        }

        /// <summary>How many degrees the LCD is turned in the live picture (the sign follows the picture, y down).</summary>
        public double TurnedDeg { get { return -Math.Atan2(N[3], N[0]) * 180.0 / Math.PI; } }

        public Mat Normalize(Mat frame) {
            var dst = new Mat();
            using (var m = new Matrix<double>(2, 3)) {
                m.Data[0, 0] = N[0]; m.Data[0, 1] = N[1]; m.Data[0, 2] = N[2];
                m.Data[1, 0] = N[3]; m.Data[1, 1] = N[4]; m.Data[1, 2] = N[5];
                CvInvoke.WarpAffine(frame, dst, m, frame.Size, Inter.Linear, Warp.Default, BorderType.Constant, new MCvScalar(0, 0, 0));
            }
            return dst;
        }

        /// <summary>Adds the small rest movement the tracker measured on the normalized picture.</summary>
        public void Update(SymbolPose pose) {
            double k = pose.Scale > 0.5 && pose.Scale < 2.0 ? pose.Scale : 1.0;                 // rest zoom: taught = (1/k) * R(-theta) * (x - Qc) + Pc
            double c = Math.Cos(-pose.ThetaRad) / k, s = Math.Sin(-pose.ThetaRad) / k;
            double a0 = c, a1 = -s, a2 = pose.PcX - (c * pose.QcX - s * pose.QcY);
            double a3 = s, a4 = c, a5 = pose.PcY - (s * pose.QcX + c * pose.QcY);
            double[] n = N;
            N = new double[] {
                a0 * n[0] + a1 * n[3], a0 * n[1] + a1 * n[4], a0 * n[2] + a1 * n[5] + a2,
                a3 * n[0] + a4 * n[3], a3 * n[1] + a4 * n[4], a3 * n[2] + a4 * n[5] + a5 };
        }

        /// <summary>
        /// Finds the LCD in the whole picture at ANY turn. The biggest symbols are turned in steps of 6 degrees (360 degrees in all) and searched
        /// in the whole picture (small copy of it, to be quick). Every find says "if the LCD is turned by this angle and shifted by this much,
        /// I am here"; all symbols vote and the angle and shift that most DIFFERENT symbols agree on wins. The rest error (up to 3 degrees)
        /// is taken out afterwards by the normal tracker. It takes some tenths of a second, so it only runs when the LCD was lost.
        /// </summary>
        public static bool FindPose(List<SymbolTemplate> tpl, Rectangle[] taught, Mat frame, double minScore, int minVotes,
                                    out double[] n, out int votes, out double angleDeg) {
            n = null; votes = 0; angleDeg = 0;
            int count = tpl.Count;

            // the biggest symbols (up to 8): they survive the small copy best
            var idx = new List<int>();
            for (int i = 0; i < count; i++) if (Math.Min(tpl[i].Size.Width, tpl[i].Size.Height) >= 16) idx.Add(i);
            idx.Sort((a, b) => (tpl[b].Size.Width * tpl[b].Size.Height).CompareTo(tpl[a].Size.Width * tpl[a].Size.Height));
            if (idx.Count > 8) idx.RemoveRange(8, idx.Count - 8);
            if (idx.Count < 2) return false;

            double sc = 0.25;
            foreach (int i in idx) if (Math.Min(tpl[i].Size.Width, tpl[i].Size.Height) * sc < 8) sc = 0.5;

            // pivot = middle of all taught symbols
            double px = 0, py = 0;
            for (int i = 0; i < count; i++) { px += taught[i].X + taught[i].Width / 2.0; py += taught[i].Y + taught[i].Height / 2.0; }
            px /= count; py /= count;

            double bestSum = -1, bestTheta = 0, bestTx = 0, bestTy = 0; int bestVotes = 0;
            var flats = new List<Mat>();
            try {
                using (Mat g = SymbolMatcher.Gray(frame))
                using (Mat gh = new Mat()) {
                    CvInvoke.Resize(g, gh, new Size(), sc, sc, Inter.Area);
                    foreach (int i in idx) flats.Add(SymbolMatcher.Flat(gh, Math.Max(2.0, tpl[i].Sigma * sc)));

                    for (int deg = 0; deg < 360; deg += 6) {
                        double th = deg * Math.PI / 180.0, c = Math.Cos(th), s = Math.Sin(th);
                        var hyp = new List<double[]>();                        // { tx, ty, score, symbol }
                        for (int k = 0; k < idx.Count; k++) {
                            int i = idx[k];
                            int w = tpl[i].Size.Width, h = tpl[i].Size.Height;
                            int D = (int)Math.Ceiling(Math.Sqrt((double)w * w + (double)h * h));
                            double cw = w / 2.0, ch = h / 2.0;
                            using (Mat rot = new Mat())
                            using (Mat rs = new Mat())
                            using (Mat res = new Mat())
                            using (var m = new Matrix<double>(2, 3)) {
                                m.Data[0, 0] = c;  m.Data[0, 1] = -s; m.Data[0, 2] = D / 2.0 - (c * cw - s * ch);
                                m.Data[1, 0] = s;  m.Data[1, 1] = c;  m.Data[1, 2] = D / 2.0 - (s * cw + c * ch);
                                CvInvoke.WarpAffine(tpl[i].Flat, rot, m, new Size(D, D), Inter.Linear, Warp.Default, BorderType.Constant, new MCvScalar(128));
                                CvInvoke.Resize(rot, rs, new Size(), sc, sc, Inter.Area);
                                if (rs.Width >= flats[k].Width || rs.Height >= flats[k].Height) continue;
                                CvInvoke.MatchTemplate(flats[k], rs, res, TemplateMatchingType.CcoeffNormed);

                                double pcx = taught[i].X + taught[i].Width / 2.0 - px, pcy = taught[i].Y + taught[i].Height / 2.0 - py;
                                double rx = px + c * pcx - s * pcy, ry = py + s * pcx + c * pcy;       // where the taught centre goes when the LCD is turned
                                for (int pk = 0; pk < 3; pk++) {
                                    double mn = 0, mx = 0; Point a = new Point(), b = new Point();
                                    CvInvoke.MinMaxLoc(res, ref mn, ref mx, ref a, ref b);
                                    if (double.IsNaN(mx) || mx < minScore) break;
                                    double qx = (b.X + rs.Width / 2.0) / sc, qy = (b.Y + rs.Height / 2.0) / sc;
                                    hyp.Add(new double[] { qx - rx, qy - ry, mx, i });
                                    CvInvoke.Rectangle(res, new Rectangle(b.X - rs.Width / 2, b.Y - rs.Height / 2, rs.Width, rs.Height), new MCvScalar(-1), -1);
                                }
                            }
                        }
                        // vote for this angle
                        foreach (double[] hh in hyp) {
                            var seen = new HashSet<int>();
                            double sx = 0, sy = 0, ss = 0; int cnt = 0;
                            foreach (double[] o in hyp)
                                if (Math.Abs(o[0] - hh[0]) <= 10.0 && Math.Abs(o[1] - hh[1]) <= 10.0 && seen.Add((int)o[3])) { sx += o[0]; sy += o[1]; ss += o[2]; cnt++; }
                            if (cnt > bestVotes || (cnt == bestVotes && ss > bestSum)) {
                                bestVotes = cnt; bestSum = ss; bestTheta = th; bestTx = sx / cnt; bestTy = sy / cnt;
                            }
                        }
                    }
                }
            } finally {
                foreach (Mat f in flats) f.Dispose();
            }

            if (bestVotes < minVotes) return false;

            // N: live picture -> taught picture   x_t = R(-theta)(x - P - t) + P
            double cc = Math.Cos(-bestTheta), ss2 = Math.Sin(-bestTheta);
            n = new double[] {
                cc, -ss2, px - (cc * (px + bestTx) - ss2 * (py + bestTy)),
                ss2, cc,  py - (ss2 * (px + bestTx) + cc * (py + bestTy)) };
            votes = bestVotes;
            angleDeg = bestTheta * 180.0 / Math.PI;
            return true;
        }
    }
}
