using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace camera_show {
    public sealed partial class CameraWorker {

        private sealed class CompareStep {
            public Mat       Template;      // config\{step}\{head}Image{n}.png (the crop saved when the ROI was drawn)
            public Rectangle Search;        // line n-1 of the {head}Rectangle*.txt files = template area + padding
        }

        /// <summary>
        /// Compare mode: for every saved template image (Image1, Image2 ...) the live picture must show ALL of it, in any light.
        /// A step passes after <see cref="TemplateOptions.StableFrames"/> good frames in a row; PASS when every step passed.
        /// What the operator sees on the tile: the yellow box = where it looks, green/red box = where it found the pattern,
        /// red cells = parts that are missing, top right = the saved TEMPLATE above what the camera shows NOW at that place,
        /// and a big PASS / FAIL when the job ends (the picture then stays on the tile). camera_show_hub.log has one line per step.
        /// </summary>
        private JobResult RunCompare(VideoCapture cap) {
            TemplateOptions opt = TemplateOptions.Load();
            string error;
            List<CompareStep> steps = BuildCompareSteps(out error);
            try {
                if (steps.Count == 0) return Fail(error);
                if (SymbolOptions.Load().Enabled) return RunCompareSymbols(cap, steps, opt);
                var total = Stopwatch.StartNew();
                int step = 0, good = 0;
                string lastNote = "no picture";
                TemplateResult last = null; Rectangle lastRoi = Rectangle.Empty;

                while (!_cancel.IsCancellationRequested) {
                    if (total.ElapsedMilliseconds >= _timeOutMs) {
                        CameraHub.Log(_job + " compare step " + (step + 1) + "/" + steps.Count + " FAIL after " + total.ElapsedMilliseconds + " ms: " + lastNote);
                        ShowCompareEnd(opt, steps[step], last, lastRoi, step, steps.Count, false);
                        return Fail("(" + (step + 1) + ")time over");
                    }

                    using (Mat frame = Grab()) {
                        if (frame == null || frame.IsEmpty) { Thread.Sleep(10); continue; }

                        Mat rotated = null, work = frame;
                        try {
                            if (opt.Rotate == 180) { using (var h = new Mat()) { CvInvoke.Flip(frame, h, FlipType.Horizontal); rotated = new Mat(); CvInvoke.Flip(h, rotated, FlipType.Vertical); } work = rotated; }   // = turned 180 deg

                            CompareStep st = steps[step];
                            Rectangle roi = Rectangle.Intersect(st.Search, new Rectangle(Point.Empty, work.Size));
                            if (roi.Width < st.Template.Width || roi.Height < st.Template.Height) {
                                lastNote = "search area (" + roi.Width + "x" + roi.Height + ") is smaller than template " + st.Template.Width + "x" + st.Template.Height;
                                Thread.Sleep(50);
                                continue;
                            }

                            TemplateResult r;
                            using (Mat region = new Mat(work, roi)) r = TemplateMatcher.Compare(st.Template, region, opt);
                            last = r; lastRoi = roi;
                            lastNote = "score " + r.Score.ToString("F2") + ", " + r.Note;

                            bool stepDone = false;
                            good = r.Pass ? good + 1 : 0;
                            if (good >= opt.StableFrames) stepDone = true;
                            bool allDone = stepDone && step + 1 >= steps.Count;

                            using (Image<Bgr, byte> img = work.ToImage<Bgr, byte>()) {
                                Draw(img, work, roi, r, st, step, steps.Count, allDone ? "PASS" : null);
                                Preview(img, allDone);                                    // the final picture is always shown
                            }

                            Status("Step " + (step + 1) + "/" + steps.Count + "  " + (r.Pass ? "match" : r.Note));
                            if (stepDone) {
                                CameraHub.Log(_job + " compare step " + (step + 1) + "/" + steps.Count + " PASS after " + total.ElapsedMilliseconds + " ms (" + lastNote + ", " + opt.StableFrames + " good frames)");
                                step++; good = 0;
                                if (step >= steps.Count) return Pass("Image Matched");
                            }
                        } finally {
                            if (rotated != null) rotated.Dispose();
                        }
                    }
                    Thread.Sleep(20);
                }
                return Fail("Cancelled");
            } finally {
                foreach (CompareStep s in steps) { try { s.Template.Dispose(); } catch { } }
            }
        }

        /// <summary>Last picture of a failed job: the red cells that never matched and a big FAIL.</summary>
        private void ShowCompareEnd(TemplateOptions opt, CompareStep st, TemplateResult last, Rectangle roi, int step, int count, bool pass) {
            try {
                if (last == null) return;
                using (Mat frame = Grab()) {
                    if (frame == null || frame.IsEmpty) return;
                    Mat rotated = null, work = frame;
                    try {
                        if (opt.Rotate == 180) { using (var h = new Mat()) { CvInvoke.Flip(frame, h, FlipType.Horizontal); rotated = new Mat(); CvInvoke.Flip(h, rotated, FlipType.Vertical); } work = rotated; }
                        using (Image<Bgr, byte> img = work.ToImage<Bgr, byte>()) {
                            Draw(img, work, roi, last, st, step, count, pass ? "PASS" : "FAIL");
                            Preview(img, true);
                        }
                    } finally { if (rotated != null) rotated.Dispose(); }
                }
            } catch { }
        }

        private void Draw(Image<Bgr, byte> img, Mat work, Rectangle roi, TemplateResult r, CompareStep st, int step, int count, string banner) {
            var green = new MCvScalar(0, 200, 0); var red = new MCvScalar(0, 0, 255); var yellow = new MCvScalar(0, 255, 255);
            var col = banner != null ? (banner == "PASS" ? green : red) : (r.Pass ? green : red);

            CvInvoke.Rectangle(img, roi, yellow, 1);                                           // where it looks
            Rectangle m = Rectangle.Empty;
            if (r.Score >= 0 && !r.Match.IsEmpty) {
                m = new Rectangle(roi.X + r.Match.X, roi.Y + r.Match.Y, r.Match.Width, r.Match.Height);
                CvInvoke.Rectangle(img, m, r.Pass ? green : red, 2);                         // where it found the pattern
                foreach (Rectangle c in r.Failed)                                              // the parts that are missing
                    CvInvoke.Rectangle(img, new Rectangle(m.X + c.X, m.Y + c.Y, c.Width, c.Height), red, 1);
            }

            CvInvoke.PutText(img, (r.Pass ? "True" : "False") + "   Head " + _job.Head + "  compare " + (step + 1) + "/" + count,
                new Point(20, 30), FontFace.HersheySimplex, 0.7, col, 2);
            CvInvoke.PutText(img, "score=" + r.Score.ToString("F2") + "  " + r.Note,
                new Point(20, 60), FontFace.HersheySimplex, 0.6, col, 2);
            CvInvoke.PutText(img, "Port = " + _port, new Point(20, 90), FontFace.HersheySimplex, 0.6, col, 2);
            if (banner != null)
                CvInvoke.PutText(img, banner, new Point(20, 160), FontFace.HersheySimplex, 2.4, col, 6);

            // top right: the saved template, and below it what the camera shows now at the place where it was found
            int maxH = Math.Max(40, img.Height / 4);
            int x = img.Width - Math.Max(60, Math.Min(img.Width / 3, st.Template.Width * maxH / Math.Max(1, st.Template.Height))) - 10;
            Inset(img, st.Template, new Point(Math.Max(0, x), 28), maxH, "TEMPLATE (saved)", yellow, null);
            if (!m.IsEmpty) {
                Rectangle ok = Rectangle.Intersect(m, new Rectangle(Point.Empty, work.Size));
                if (ok.Width > 4 && ok.Height > 4)
                    using (Mat live = new Mat(work, ok))
                        Inset(img, live, new Point(Math.Max(0, x), 28 + maxH + 28), maxH, "LIVE NOW", col, r.Failed);
            }
        }

        /// <summary>Draws a small copy of a picture (template or live crop) into the tile picture, with a frame, a label and red cells.</summary>
        private static void Inset(Image<Bgr, byte> img, Mat pic, Point origin, int maxH, string label, MCvScalar col, List<Rectangle> marks) {
            try {
                if (pic == null || pic.IsEmpty) return;
                double s = Math.Min(1.0, (double)maxH / pic.Height);
                s = Math.Min(s, (img.Width / 3.0) / pic.Width);
                var sz = new Size(Math.Max(2, (int)(pic.Width * s)), Math.Max(2, (int)(pic.Height * s)));
                var dst = new Rectangle(origin, sz);
                if (!new Rectangle(Point.Empty, img.Size).Contains(dst)) return;

                using (Mat small = new Mat())
                using (Mat bgr = new Mat()) {
                    if (pic.NumberOfChannels == 1) CvInvoke.CvtColor(pic, bgr, ColorConversion.Gray2Bgr); else pic.CopyTo(bgr);
                    CvInvoke.Resize(bgr, small, sz);
                    using (Mat target = new Mat(img.Mat, dst)) small.CopyTo(target);
                }
                if (marks != null)
                    foreach (Rectangle c in marks)
                        CvInvoke.Rectangle(img, new Rectangle(origin.X + (int)(c.X * s), origin.Y + (int)(c.Y * s), Math.Max(1, (int)(c.Width * s)), Math.Max(1, (int)(c.Height * s))), new MCvScalar(0, 0, 255), 1);
                CvInvoke.Rectangle(img, dst, col, 1);
                CvInvoke.PutText(img, label, new Point(origin.X, origin.Y - 6), FontFace.HersheySimplex, 0.45, col, 1);
            } catch { }
        }

        /// <summary>Template n = config\{step}\{head}Image{n}.png, its search area = line n-1 of the Rectangle files (step 1: CSV ROI when missing).</summary>
        private List<CompareStep> BuildCompareSteps(out string error) {
            error = null;
            var steps = new List<CompareStep>();
            string dir = StepConfig.ConfigDir + _job.StepName + "/";
            string pre = dir + _job.Head;
            Rectangle prev = _rect;

            for (int n = 1; File.Exists(pre + "Image" + n + ".png"); n++) {
                Mat t = null;
                try { t = CvInvoke.Imread(pre + "Image" + n + ".png", ImreadModes.AnyColor); } catch { }
                if (t == null || t.IsEmpty || t.Width < 12 || t.Height < 12) {
                    if (t != null) t.Dispose();
                    foreach (CompareStep s in steps) s.Template.Dispose();
                    steps.Clear();
                    error = "Template " + n + " is not a picture (" + pre + "Image" + n + ".png)";
                    return steps;
                }

                Rectangle r = prev;     // a bad / empty line falls back to the previous area
                try {
                    r = new Rectangle(
                        Convert.ToInt32(File.ReadAllLines(pre + AppFilePath.RectX)     [n - 1]),
                        Convert.ToInt32(File.ReadAllLines(pre + AppFilePath.RectY)     [n - 1]),
                        Convert.ToInt32(File.ReadAllLines(pre + AppFilePath.RectWidth) [n - 1]),
                        Convert.ToInt32(File.ReadAllLines(pre + AppFilePath.RectHeight)[n - 1]));
                } catch { }
                prev = r;
                steps.Add(new CompareStep { Template = t, Search = r });
            }

            if (steps.Count == 0) error = "No template picture: draw the ROI in this mode first (" + pre + "Image1.png)";
            return steps;
        }
    }
}
