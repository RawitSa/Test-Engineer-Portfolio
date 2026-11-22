using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace camera_show {
    public sealed partial class CameraWorker {

        /// <summary>
        /// Compare, per-symbol version. Every saved step (Image1.png, Image2.png ...) is ONE symbol, taught by drawing a box around it
        /// (the box is the symbol, the padding added at teach time is the search window). All symbols are checked in every frame,
        /// at the place where the tracker finds the LCD now (the boxes follow the workpiece).
        /// PASS = every symbol passes together for hold_ms without a break. When time is over the job FAILS and says which symbols are missing.
        /// </summary>
        private JobResult RunCompareSymbols(VideoCapture cap, List<CompareStep> steps, TemplateOptions topt) {
            SymbolOptions opt = SymbolOptions.Load();
            SymbolDebug.Enabled = opt.Debug;
            var tpl = new List<SymbolTemplate>();
            SymbolAI ai = null;
            if (opt.AiMode != 0) {
                string aiWhy;
                ai = SymbolAI.Load(steps.Count, out aiWhy);
                CameraHub.Log(_job + " compare SYMBOLS AI: " + (ai != null ? ai.Info + ", mode " + opt.AiMode + ", ai_min " + opt.AiMin.ToString("F2") : "not used - " + aiWhy));
            }
            try {
                foreach (CompareStep s in steps) tpl.Add(SymbolTemplate.Build(s.Template, opt.Grid));

                SymbolOptions[] perSym = SymbolCalib.Apply(opt, steps.Count);          // limits per symbol (learned), or the defaults
                SymbolCalib learner = opt.Learn ? new SymbolCalib(steps.Count) : null;

                var total = Stopwatch.StartNew();
                var held = new Stopwatch();
                var dbg = Stopwatch.StartNew();                         // pace of the debug pictures
                var results = new SymbolResult[steps.Count];
                string lastNote = "no picture";
                SymbolPose lastPose = null;
                double[] lastN = null;                                  // the movement used for the last picture (to draw the boxes on the real picture)

                // where each symbol sits in the taught picture (the template, without the padding)
                var taught = new Rectangle[steps.Count];
                for (int i = 0; i < steps.Count; i++) {
                    int px = (steps[i].Search.Width - tpl[i].Size.Width) / 2;
                    int py = (steps[i].Search.Height - tpl[i].Size.Height) / 2;
                    taught[i] = new Rectangle(steps[i].Search.X + px, steps[i].Search.Y + py, tpl[i].Size.Width, tpl[i].Size.Height);
                }
                // sanity check of the teaching files: when the {head}Rectangle*.txt files were not copied together with the Image*.png
                // (e.g. the pictures of one Compare mode copied into another mode's folder), every step falls back to the SAME search area,
                // all symbols then sit at one place and the tracker can never match them. Say so, in the log and on the picture.
                string setupWarn = null;
                var places = new HashSet<string>();
                for (int i = 0; i < steps.Count; i++) places.Add((taught[i].X + taught[i].Width / 2) + "," + (taught[i].Y + taught[i].Height / 2));
                CameraHub.Log(_job + " compare SYMBOLS mode: " + steps.Count + " symbols from " + StepConfig.ConfigDir + _job.StepName + "/" + _job.Head
                    + "Image*.png, " + places.Count + " different places");
                if (steps.Count > 1 && places.Count < steps.Count)
                    setupWarn = "WARNING: " + steps.Count + " symbols but only " + places.Count + " different places - copy " + _job.Head + "RectangleX/Y/Width/Height.txt with the images";
                var findClock = new Stopwatch();                        // the whole-picture search is slow: not on every frame
                int scaleTry = 0;                                       // which zoom the whole-picture search tries next (lens changed / camera zoomed)
                const int Track = 4;                                   // px around the expected place of a symbol

                var aligner = new SymbolAligner();                     // how the workpiece is moved / turned now (any angle)
                int minIn = Math.Max(3, steps.Count / 5);              // symbols that must agree on the movement

                while (!_cancel.IsCancellationRequested) {
                    if (total.ElapsedMilliseconds >= _timeOutMs) {
                        string missing = MissingList(results);
                        CameraHub.Log(_job + " compare SYMBOLS FAIL after " + total.ElapsedMilliseconds + " ms: " + missing + " | " + ScoreLine(results));
                        SymbolDebug.Snapshot("fail");
                        ShowSymbolsEnd(topt, steps, results, "FAIL", lastPose, lastN);
                        return Fail("symbol missing: " + missing);
                    }

                    using (Mat frame = Grab()) {
                        if (frame == null || frame.IsEmpty) { Thread.Sleep(10); continue; }

                        Mat rotated = null, work = frame;
                        try {
                            if (topt.Rotate == 180) { using (var h = new Mat()) { CvInvoke.Flip(frame, h, FlipType.Horizontal); rotated = new Mat(); CvInvoke.Flip(h, rotated, FlipType.Vertical); } work = rotated; }

                            // 1. INSIDE only: a copy of the picture turned back to the taught position, so tracker and check work as taught.
                            //    The operator never sees it: the screen shows the real picture with the boxes turned onto the workpiece.
                            Mat norm = aligner.Normalize(work);
                            double[] nUsed = (double[])aligner.N.Clone();
                            try {
                                SymbolPose pose = SymbolTracker.Locate(tpl, taught, norm, 40, 0.6, minIn);
                                if (pose.Ok) {
                                    aligner.Update(pose);                                 // small movement: follow it
                                } else {
                                    // lost (moved far / turned): look in the whole picture at every angle
                                    double[] nn = null; int votes = 0; double ang = 0;
                                    bool found = false;
                                    if (!findClock.IsRunning || findClock.ElapsedMilliseconds >= 300) {
                                        findClock.Restart();
                                        found = FindPoseAnyZoom(tpl, taught, work, ref scaleTry, out nn, out votes, out ang);
                                    }
                                    if (found) {
                                        aligner.N = nn;
                                        norm.Dispose(); norm = aligner.Normalize(work);
                                        nUsed = (double[])aligner.N.Clone();
                                        pose = SymbolTracker.Locate(tpl, taught, norm, 40, 0.6, minIn);
                                        if (pose.Ok) aligner.Update(pose);
                                        else pose.Note = "found LCD (" + ang.ToString("F0") + " deg) but cannot confirm: " + pose.Note;
                                    } else {
                                        pose.Note = "searching for the LCD at every angle... (" + pose.Note + ")";
                                    }
                                }
                                if (!pose.Ok && setupWarn != null) pose.Note = setupWarn + " | " + pose.Note;
                                lastPose = pose; lastN = nUsed;
                                SymbolPoseBus.Set(_job.Head, pose.Ok ? aligner.N : null);      // lets a re-drawn ROI be saved in taught coordinates

                                // 2. check every symbol where it is expected now
                                bool all = pose.Ok;
                                Rectangle fr = new Rectangle(Point.Empty, norm.Size);
                                AiResult[] aiRes = (pose.Ok && ai != null) ? ai.Evaluate(norm, pose.Expected, opt.AiMin, opt.AiShift) : null;
                                for (int i = 0; i < steps.Count; i++) {
                                    if (!pose.Ok) { SetResult(results, i, new SymbolResult { Note = pose.Note }); continue; }
                                    Rectangle roi = Rectangle.Intersect(Rectangle.Inflate(pose.Expected[i], Track, Track), fr);
                                    SymbolResult r;
                                    if (roi.Width < tpl[i].Size.Width || roi.Height < tpl[i].Size.Height) {
                                        r = new SymbolResult { Note = "out of picture" };
                                    } else {
                                        using (Mat region = new Mat(norm, roi)) r = SymbolMatcher.Check(tpl[i], region, perSym[i]);
                                    }
                                    if (aiRes != null && i < aiRes.Length && aiRes[i] != null && aiRes[i].Valid) FuseAi(r, aiRes[i], opt, i);
                                    SetResult(results, i, r);
                                    if (!r.Pass) all = false;
                                }

                                // 3. all together for hold_ms
                                if (all) { if (!held.IsRunning) held.Restart(); } else held.Reset();
                                bool done = learner == null && all && held.ElapsedMilliseconds >= opt.HoldMs;
                                lastNote = pose.Ok ? (all ? "all " + steps.Count + " ok" : MissingList(results) + " missing") : pose.Note;

                                // learning mode: collect the scores of a GOOD unit, then write camera_show_calib.txt
                                if (learner != null) {
                                    if (pose.Ok) learner.Add(results);
                                    lastNote = "LEARNING " + learner.Frames + "/" + opt.LearnFrames + " (keep a good unit in view, move / turn it a little)";
                                }

                                // the operator sees the REAL camera picture; the boxes are turned onto the workpiece
                                using (Image<Bgr, byte> img = work.ToImage<Bgr, byte>()) {
                                    DrawLive(img, steps, results, pose, nUsed, aligner.TurnedDeg, done ? "PASS" : null);
                                    Preview(img, done);
                                    // real-time debug pictures: light-removed frame and a comparison for EVERY symbol
                                    if (opt.Debug && (done || dbg.ElapsedMilliseconds >= 400)) {
                                        dbg.Restart();
                                        SymbolDebug.Dump(work, norm, tpl, results, perSym, img, done ? "pass" : null);
                                    }
                                }
                                Status(lastNote);

                                if (learner != null && learner.Frames >= opt.LearnFrames) {
                                    bool saved = learner.Save();
                                    CameraHub.Log(_job + " compare SYMBOLS learned from " + learner.Frames + " frames -> " + SymbolCalib.FilePath + (saved ? "" : " (could not write)"));
                                    return saved ? Pass("Calibrated: set sym_learn=0") : Fail("Calibration could not be saved");
                                }

                                if (done) {
                                    CameraHub.Log(_job + " compare SYMBOLS PASS after " + total.ElapsedMilliseconds + " ms | turned " + aligner.TurnedDeg.ToString("F0") + " deg | " + ScoreLine(results));
                                    return Pass("Symbols Matched");
                                }
                            } catch (Exception ex) {
                                CameraHub.Log(_job + " compare SYMBOLS error: " + ex);
                                lastNote = "ERROR: " + ex.GetType().Name + ": " + ex.Message;
                                using (Image<Bgr, byte> img = work.ToImage<Bgr, byte>()) {
                                    CvInvoke.PutText(img, lastNote, new Point(20, 40), FontFace.HersheySimplex, 0.7, new MCvScalar(0, 0, 255), 2);
                                    Preview(img, false);
                                }
                                Status(lastNote);
                            } finally {
                                norm.Dispose();
                            }
                        } finally {
                            if (rotated != null) rotated.Dispose();
                        }
                    }
                    Thread.Sleep(20);
                }
                return Fail("Cancelled");
            } finally {
                foreach (SymbolTemplate t in tpl) { try { t.Dispose(); } catch { } }
            }
        }

        /// <summary>Combines the AI answer with the classical one (ai= in camera_show_template.txt) and keeps crops for re-training.</summary>
        private static void FuseAi(SymbolResult r, AiResult a, SymbolOptions o, int i) {
            bool aiPass = a.Score >= o.AiMin, classic = r.Pass;
            r.Ai = a.Score;
            if (o.AiMode == 2) r.Pass = aiPass && classic;
            else if (o.AiMode == 3) r.Pass = aiPass || classic;
            else r.Pass = aiPass;
            if (!r.Pass) r.Note = "AI " + a.Score.ToString("F2") + " (sees " + a.BestName + ")" + (classic || string.IsNullOrEmpty(r.Note) ? "" : " | " + r.Note);
            if (o.AiCollect) {
                if (aiPass != classic) SymbolAI.Collect(a, i, "disagree");
                else if (a.Score > 0.2 && a.Score < o.AiMin) SymbolAI.Collect(a, i, "unsure");
            }
        }

        private static void SetResult(SymbolResult[] a, int i, SymbolResult r) {
            if (a[i] != null) a[i].Dispose();
            a[i] = r;
        }

        private static string MissingList(SymbolResult[] results) {
            var sb = new StringBuilder();
            for (int i = 0; i < results.Length; i++)
                if (results[i] == null || !results[i].Pass) { if (sb.Length > 0) sb.Append(','); sb.Append('S').Append(i + 1); }
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        /// <summary>One log line with the scores of every symbol: use it to calibrate sym_zncc / sym_edge / sym_contrast.</summary>
        private static string ScoreLine(SymbolResult[] results) {
            var sb = new StringBuilder("SYMBOL");
            for (int i = 0; i < results.Length; i++) {
                SymbolResult r = results[i];
                if (r == null) { sb.Append(" S").Append(i + 1).Append("=n/a"); continue; }
                sb.Append(" S").Append(i + 1).Append('=')
                  .Append(r.S1.ToString("F2")).Append('/').Append(r.S2.ToString("F2")).Append('/').Append(r.C.ToString("F2")).Append('/').Append(r.T.ToString("F2")).Append('/').Append(r.G.ToString("F2"))
                  .Append(r.Ai >= 0 ? "/A" + r.Ai.ToString("F2") : "").Append(r.Pass ? "" : "!");
            }
            return sb.ToString();
        }

        private void ShowSymbolsEnd(TemplateOptions topt, List<CompareStep> steps, SymbolResult[] results, string banner, SymbolPose pose, double[] n) {
            try {
                using (Mat frame = Grab()) {
                    if (frame == null || frame.IsEmpty) return;
                    Mat rotated = null, work = frame;
                    try {
                        if (topt.Rotate == 180) { using (var h = new Mat()) { CvInvoke.Flip(frame, h, FlipType.Horizontal); rotated = new Mat(); CvInvoke.Flip(h, rotated, FlipType.Vertical); } work = rotated; }
                        using (Image<Bgr, byte> img = work.ToImage<Bgr, byte>()) {
                            DrawLive(img, steps, results, pose, n, 0, banner);
                            Preview(img, true);
                        }
                    } finally { if (rotated != null) rotated.Dispose(); }
                }
            } catch { }
        }

        /// <summary>
        /// Draws on the REAL camera picture: the result in the corner and, for every symbol, a box turned onto the workpiece
        /// (green = symbol ok, red = not ok). n = the movement used for this picture (picture -> taught position); the boxes are
        /// found in the taught position and brought back to the picture with its inverse.
        /// </summary>
        private void DrawLive(Image<Bgr, byte> img, List<CompareStep> steps, SymbolResult[] results, SymbolPose pose, double[] n, double turnedDeg, string banner) {
            var green = new MCvScalar(0, 200, 0); var red = new MCvScalar(0, 0, 255); var orange = new MCvScalar(0, 165, 255);
            int bad = 0;
            for (int i = 0; i < steps.Count; i++) if (results[i] == null || !results[i].Pass) bad++;
            bool ok = bad == 0;
            bool aiOn = false;
            for (int i = 0; i < results.Length; i++) if (results[i] != null && results[i].Ai >= 0) aiOn = true;
            var head = banner != null ? (banner == "PASS" ? green : red) : (ok ? green : red);

            CvInvoke.PutText(img, (ok ? "True" : "False") + "   Head " + _job.Head + "  symbols " + (steps.Count - bad) + "/" + steps.Count + (aiOn ? "  [AI]" : ""),
                new Point(20, 30), FontFace.HersheySimplex, 0.7, head, 2);
            if (!ok) CvInvoke.PutText(img, "missing: " + MissingList(results), new Point(20, 60), FontFace.HersheySimplex, 0.6, red, 2);
            CvInvoke.PutText(img, "Port = " + _port, new Point(20, 90), FontFace.HersheySimplex, 0.6, head, 2);

            if (pose != null && pose.Ok && pose.Expected != null && n != null) {
                double[] inv = InvertN(n);
                Rectangle fr = new Rectangle(Point.Empty, img.Size);
                for (int i = 0; i < pose.Expected.Length && i < results.Length; i++) {
                    bool pass = results[i] != null && results[i].Pass;
                    var col = pass ? green : red;
                    Point[] q = Quad(pose.Expected[i], inv);
                    CvInvoke.Polylines(img, q, true, col, pass ? 2 : 3);
                    bool inside = true;
                    foreach (Point pt in q) if (!fr.Contains(pt)) inside = false;
                    CvInvoke.PutText(img, "S" + (i + 1) + (results[i] != null && results[i].Ai >= 0 ? " " + results[i].Ai.ToString("0.00") : "") + (inside ? "" : " out of view"), new Point(q[0].X, Math.Max(12, q[0].Y - 4)),
                        FontFace.HersheySimplex, 0.45, col, 1);
                }
                CvInvoke.PutText(img, "tracking  LCD turned " + turnedDeg.ToString("F0") + " deg  (" + pose.Inliers + " symbols agree)",
                    new Point(20, 120), FontFace.HersheySimplex, 0.5, green, 1);
            } else if (pose != null) {
                CvInvoke.PutText(img, pose.Note, new Point(20, 120), FontFace.HersheySimplex, 0.6, orange, 2);
            }
            if (banner != null) CvInvoke.PutText(img, banner, new Point(20, 190), FontFace.HersheySimplex, 2.4, head, 6);
        }

        /// <summary>
        /// Whole-picture search that also copes with a changed zoom (new lens, zoom ring turned): every call tries ONE zoom factor
        /// (the last good one first, then 1.0, then 6 % steps both ways) so the picture stays responsive. The factor that works
        /// is remembered (SymbolAligner.LastScale) and becomes part of N, so the taught boxes and templates fit again at their taught size.
        /// </summary>
        private bool FindPoseAnyZoom(List<SymbolTemplate> tpl, Rectangle[] taught, Mat work, ref int tryNo, out double[] nn, out int votes, out double ang) {
            // The normal search (last good zoom, any angle) is what follows a turned workpiece, so it must keep running at full speed.
            // Other zooms are only scanned when the LCD has been lost for ~2 s, and then only every second try.
            double[] order = SymbolAligner.ScaleOrder();
            double k = order[0];
            if (tryNo >= 6 && tryNo % 2 == 1 && order.Length > 1) k = order[1 + ((tryNo - 7) / 2) % (order.Length - 1)];
            nn = null; votes = 0; ang = 0;
            bool found;
            if (Math.Abs(k - 1.0) < 0.001) {
                found = SymbolAligner.FindPose(tpl, taught, work, 0.5, 3, out nn, out votes, out ang);
            } else {
                using (Mat big = new Mat()) {
                    CvInvoke.Resize(work, big, new Size(), k, k, k > 1.0 ? Inter.Linear : Inter.Area);
                    found = SymbolAligner.FindPose(tpl, taught, big, 0.5, 3, out nn, out votes, out ang);
                    if (found) nn = new double[] { nn[0] * k, nn[1] * k, nn[2], nn[3] * k, nn[4] * k, nn[5] };   // live -> scaled live -> taught
                }
            }
            if (found) {
                if (Math.Abs(k - SymbolAligner.LastScale) > 0.005) CameraHub.Log(_job + " compare SYMBOLS: LCD found at zoom x" + k.ToString("F2") + " (the picture is zoomed against the taught one; boxes follow it)");
                SymbolAligner.LastScale = k; tryNo = 0;
            } else {
                tryNo++;
            }
            return found;
        }

        /// <summary>Inverse of the 2x3 matrix (picture -> taught position): taught position -> picture.</summary>
        private static double[] InvertN(double[] n) {
            double det = n[0] * n[4] - n[1] * n[3];
            if (Math.Abs(det) < 1e-9) return new double[] { 1, 0, 0, 0, 1, 0 };
            double i0 = n[4] / det, i1 = -n[1] / det, i3 = -n[3] / det, i4 = n[0] / det;
            return new double[] { i0, i1, -(i0 * n[2] + i1 * n[5]), i3, i4, -(i3 * n[2] + i4 * n[5]) };
        }

        /// <summary>The 4 corners of a rectangle of the taught position, brought onto the real picture.</summary>
        private static Point[] Quad(Rectangle r, double[] m) {
            double[,] c = { { r.X, r.Y }, { r.Right, r.Y }, { r.Right, r.Bottom }, { r.X, r.Bottom } };
            var q = new Point[4];
            for (int k = 0; k < 4; k++)
                q[k] = new Point((int)Math.Round(m[0] * c[k, 0] + m[1] * c[k, 1] + m[2]), (int)Math.Round(m[3] * c[k, 0] + m[4] * c[k, 1] + m[5]));
            return q;
        }
    }
}
