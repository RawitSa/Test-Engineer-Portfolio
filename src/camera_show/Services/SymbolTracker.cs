using System;
using System.Collections.Generic;
using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace camera_show {
    /// <summary>Where the symbols are expected in THIS frame (after the workpiece moved / turned a little).</summary>
    public sealed class SymbolPose {
        public bool        Ok;
        public string      Note = string.Empty;
        public double      Dx, Dy, AngleDeg;     // movement of the whole LCD against the taught picture
        public int         Inliers;              // how many symbols agreed on that movement
        public double      Scale = 1;            // the rest zoom of the LCD against the taught picture (1 = same size)
        public double      ThetaRad, PcX, PcY, QcX, QcY;   // the rest movement: taught centre (Pc) -> found centre (Qc), turned by Theta (used by SymbolAligner.Update)
        public Rectangle[] Expected;             // per symbol: where its template should sit now (only when Ok)
        public Rectangle[] Found;                // per symbol: best match of the wide search (Empty when nothing good was found)
        public double[]    FoundScore;           // its score
        public bool[]      Inlier;               // true = this symbol agreed with the others on the movement
    }

    /// <summary>
    /// Finds the LCD again after the workpiece moved, WITHOUT any extra teaching.
    /// 1. every symbol is searched in a wide window (maxShift px around where it was taught), by correlation of the light-corrected picture
    /// 2. the symbols that were found with a good score vote for ONE movement (median dx, dy). A hidden / missing symbol that matches
    ///    something else somewhere else disagrees with the others, so it is thrown out as an outlier and can NOT drag the pose
    /// 3. rotation is refined from the agreeing symbols (least squares), then every symbol gets its expected position
    /// If too few symbols agree (hand over the LCD, wrong part) the pose is not Ok and the job must FAIL.
    /// </summary>
    public static class SymbolTracker {

        public static SymbolPose Locate(List<SymbolTemplate> tpl, Rectangle[] taught, Mat frame,
                                        int maxShift, double minScore, int minInliers) {
            var pose = new SymbolPose();
            int n = tpl.Count;
            pose.Found = new Rectangle[n]; pose.FoundScore = new double[n]; pose.Inlier = new bool[n];
            var p = new PointF[n]; var q = new PointF[n]; var found = new bool[n];
            Rectangle fr = new Rectangle(Point.Empty, frame.Size);

            for (int i = 0; i < n; i++) {
                p[i] = new PointF(taught[i].X + taught[i].Width / 2f, taught[i].Y + taught[i].Height / 2f);
                Rectangle wide = Rectangle.Intersect(Rectangle.Inflate(taught[i], maxShift, maxShift), fr);
                if (wide.Width < tpl[i].Size.Width || wide.Height < tpl[i].Size.Height) continue;
                using (Mat region = new Mat(frame, wide))
                using (Mat g = SymbolMatcher.Gray(region))
                using (Mat f = SymbolMatcher.Flat(g, tpl[i].Sigma))
                using (Mat res = new Mat()) {
                    CvInvoke.MatchTemplate(f, tpl[i].Flat, res, TemplateMatchingType.CcoeffNormed);
                    double mn = 0, mx = 0; Point a = new Point(), b = new Point();
                    CvInvoke.MinMaxLoc(res, ref mn, ref mx, ref a, ref b);
                    if (double.IsNaN(mx) || double.IsInfinity(mx) || mx < minScore) continue;
                    q[i] = new PointF(wide.X + b.X + tpl[i].Size.Width / 2f, wide.Y + b.Y + tpl[i].Size.Height / 2f);
                    found[i] = true;
                    pose.Found[i] = new Rectangle(wide.X + b.X, wide.Y + b.Y, tpl[i].Size.Width, tpl[i].Size.Height);
                    pose.FoundScore[i] = mx;
                }
            }

            // vote: median movement
            var dxs = new List<double>(); var dys = new List<double>();
            for (int i = 0; i < n; i++) if (found[i]) { dxs.Add(q[i].X - p[i].X); dys.Add(q[i].Y - p[i].Y); }
            if (dxs.Count < minInliers) { pose.Note = "alignment lost: only " + dxs.Count + " of " + n + " symbols found"; return pose; }
            double mdx = Median(dxs), mdy = Median(dys);

            const double tol = 6.0;                                        // px: a symbol further than this from the vote is an outlier
            var inl = new List<int>();
            for (int i = 0; i < n; i++)
                if (found[i] && Math.Abs(q[i].X - p[i].X - mdx) <= tol && Math.Abs(q[i].Y - p[i].Y - mdy) <= tol) inl.Add(i);
            foreach (int i in inl) pose.Inlier[i] = true;
            if (inl.Count < minInliers) { pose.Note = "alignment lost: only " + inl.Count + " of " + n + " symbols agree"; return pose; }

            // refine: centroid + rotation from the agreeing symbols
            double pcx = 0, pcy = 0, qcx = 0, qcy = 0;
            foreach (int i in inl) { pcx += p[i].X; pcy += p[i].Y; qcx += q[i].X; qcy += q[i].Y; }
            pcx /= inl.Count; pcy /= inl.Count; qcx /= inl.Count; qcy /= inl.Count;
            double theta = 0, scl = 1, zDen = 0;
            if (inl.Count >= 4) {
                double cross = 0, dot = 0, den = 0;
                foreach (int i in inl) {
                    double ax = p[i].X - pcx, ay = p[i].Y - pcy, bx = q[i].X - qcx, by = q[i].Y - qcy;
                    cross += ax * by - ay * bx; dot += ax * bx + ay * by; den += ax * ax + ay * ay;
                }
                theta = Math.Atan2(cross, dot); zDen = den;
                if (den > 1e-6) scl = Math.Sqrt(cross * cross + dot * dot) / den;      // least-squares zoom (similarity fit)
            }
            // The zoom only changes when the lens is changed. Follow a small rest zoom, but only from many symbols that are far apart
            // (while the workpiece is being turned the match of a few symbols is noisy and would shake the boxes).
            double lever = inl.Count > 0 ? Math.Sqrt(zDen / inl.Count) : 0;
            if (inl.Count < 8 || lever < 40 || Math.Abs(scl - 1.0) < 0.012 || Math.Abs(scl - 1.0) > 0.08) scl = 1.0;
            if (Math.Abs(theta) > 8.0 * Math.PI / 180.0) { pose.Note = "workpiece turned too much (" + (theta * 180 / Math.PI).ToString("F1") + " deg)"; return pose; }

            double c = Math.Cos(theta), s = Math.Sin(theta);
            pose.Expected = new Rectangle[n];
            for (int j = 0; j < n; j++) {
                double ax = p[j].X - pcx, ay = p[j].Y - pcy;
                double cx = qcx + scl * (c * ax - s * ay), cy = qcy + scl * (s * ax + c * ay);
                pose.Expected[j] = new Rectangle((int)Math.Round(cx - tpl[j].Size.Width / 2.0), (int)Math.Round(cy - tpl[j].Size.Height / 2.0),
                                                 tpl[j].Size.Width, tpl[j].Size.Height);
            }
            pose.Scale = scl;
            pose.ThetaRad = theta; pose.PcX = pcx; pose.PcY = pcy; pose.QcX = qcx; pose.QcY = qcy;
            pose.Dx = qcx - pcx; pose.Dy = qcy - pcy; pose.AngleDeg = theta * 180.0 / Math.PI; pose.Inliers = inl.Count;
            pose.Ok = true;
            return pose;
        }

        /// <summary>
        /// Global search, for when the workpiece was moved far (more than the narrow search of Locate). Looks for the symbols in the WHOLE picture
        /// (at half size, to be quick), keeps the best few places of every symbol and lets all symbols vote for ONE shift of the whole LCD.
        /// Symbols that look alike (digits) give several guesses, but only the true shift is supported by all the different symbols together.
        /// Returns the shift of the LCD against the taught picture; then call Locate with the taught rectangles moved by that shift.
        /// </summary>
        public static bool FindShift(List<SymbolTemplate> tpl, Rectangle[] taught, Mat frame, double minScore, int minVotes, out Point shift, out int votes) {
            shift = Point.Empty; votes = 0;
            const double scale = 0.5;
            const double tol = 10.0;                                       // px: guesses closer than this count as the same shift
            var hyp = new List<double[]>();                                // { dx, dy, score, symbol }

            using (Mat g = SymbolMatcher.Gray(frame))
            using (Mat gh = new Mat()) {
                CvInvoke.Resize(g, gh, new Size(), scale, scale, Inter.Area);
                for (int i = 0; i < tpl.Count; i++) {
                    Size ts = new Size((int)Math.Round(tpl[i].Size.Width * scale), (int)Math.Round(tpl[i].Size.Height * scale));
                    if (ts.Width < 10 || ts.Height < 10 || ts.Width >= gh.Width || ts.Height >= gh.Height) continue;
                    using (Mat th = new Mat())
                    using (Mat fh = SymbolMatcher.Flat(gh, Math.Max(2.0, tpl[i].Sigma * scale)))
                    using (Mat res = new Mat()) {
                        CvInvoke.Resize(tpl[i].Flat, th, ts, 0, 0, Inter.Area);
                        CvInvoke.MatchTemplate(fh, th, res, TemplateMatchingType.CcoeffNormed);
                        double pcx = taught[i].X + taught[i].Width / 2.0, pcy = taught[i].Y + taught[i].Height / 2.0;
                        for (int k = 0; k < 4; k++) {                      // the 4 best places of this symbol
                            double mn = 0, mx = 0; Point a = new Point(), b = new Point();
                            CvInvoke.MinMaxLoc(res, ref mn, ref mx, ref a, ref b);
                            if (double.IsNaN(mx) || mx < minScore) break;
                            double cx = (b.X + ts.Width / 2.0) / scale, cy = (b.Y + ts.Height / 2.0) / scale;
                            hyp.Add(new double[] { cx - pcx, cy - pcy, mx, i });
                            CvInvoke.Rectangle(res, new Rectangle(b.X - ts.Width / 2, b.Y - ts.Height / 2, ts.Width, ts.Height), new MCvScalar(-1), -1);   // look for the next place
                        }
                    }
                }
            }

            int bestVotes = 0; double bestSum = -1, bx = 0, by = 0;
            foreach (double[] h in hyp) {
                var seen = new HashSet<int>();
                double sx = 0, sy = 0, ss = 0; int cnt = 0;
                foreach (double[] o in hyp)
                    if (Math.Abs(o[0] - h[0]) <= tol && Math.Abs(o[1] - h[1]) <= tol && seen.Add((int)o[3])) { sx += o[0]; sy += o[1]; ss += o[2]; cnt++; }
                if (cnt > bestVotes || (cnt == bestVotes && ss > bestSum)) { bestVotes = cnt; bestSum = ss; bx = sx / cnt; by = sy / cnt; }
            }
            if (bestVotes < minVotes) return false;
            shift = new Point((int)Math.Round(bx), (int)Math.Round(by));
            votes = bestVotes;
            return true;
        }

        private static double Median(List<double> v) {
            var a = v.ToArray(); Array.Sort(a);
            int m = a.Length / 2;
            return a.Length % 2 == 1 ? a[m] : (a[m - 1] + a[m]) / 2.0;
        }
    }
}
