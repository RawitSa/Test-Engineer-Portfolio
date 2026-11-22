using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace camera_show {
    public sealed partial class CameraWorker {

        /// <summary>
        /// Multi-LED mode (any step name containing "multi_led" or "multiled"): ALL the chosen LEDs are looked at in the same picture.
        /// Every LED has its own ROI (config\{mode}\{head}Image{n}.png = marker, line n of the {head}Rectangle*.txt files = the box).
        /// Which LEDs count is set in led_select.txt (LedSelect): e.g.  on=1-3,5-7,9-10  -> those 8 LEDs must be lit at the same time, for the
        /// hold time of the colour settings; LED 4 and 8 are not looked at. No file = every taught LED must be lit.
        /// A LED is lit when at least "mask" pixels of its box are inside the colour range (the same colour settings as Check LED).
        /// Tile: green box = lit as wanted, red box = should be lit (or dark) but is not, grey box = not checked.
        /// </summary>
        private JobResult RunMultiLed(VideoCapture cap) {
            string dir = StepConfig.ConfigDir + _job.StepName + "/";
            string error;
            List<Rectangle> rois = BuildMultiLedRects(dir, out error);
            if (rois.Count == 0) return Fail("No LED taught: draw the ROI of LED 1, 2, 3 ... (files in config\\" + _job.StepName + ")");

            LedSelect sel = LedSelect.Load(dir, _job.Head);
            if (sel.Error != null) return Fail("led_select.txt: " + sel.Error);

            // on=all means every LED that was TAUGHT (a number that was skipped when drawing, e.g. no LED 4, is simply not there)
            List<int> on = sel.All ? Enumerable.Range(1, rois.Count).Where(n => !rois[n - 1].IsEmpty && !sel.Off.Contains(n)).ToList()
                                   : sel.On.ToList();
            List<int> off = sel.Off.ToList();
            var noRoi = new List<int>();
            foreach (int n in on.Concat(off)) if (n > rois.Count || rois[n - 1].IsEmpty) noRoi.Add(n);
            if (noRoi.Count > 0) return Fail("LED " + LedSelect.Format(noRoi) + " is selected but its ROI is not taught");
            if (on.Count == 0 && off.Count == 0) return Fail("No LED selected in led_select.txt");

            int holdMs = sel.HoldMs >= 0 ? sel.HoldMs : _hsvTimeout;
            CameraHub.Log(_job + " multi-LED: " + rois.Count + " LED ROI taught, must be ON: " + LedSelect.Format(on) +
                          (off.Count > 0 ? ", must be OFF: " + LedSelect.Format(off) : "") + ", mask " + _hsvMask + " px, hold " + holdMs + " ms (" + sel.Source + ")");

            var total = Stopwatch.StartNew();
            var hold = Stopwatch.StartNew();                 // how long the wanted state has been there without a break
            string lastBad = "";
            string lastStatus = "";

            while (!_cancel.IsCancellationRequested) {
                if (total.ElapsedMilliseconds >= _timeOutMs)
                    return Fail("(time over) " + (lastBad.Length > 0 ? lastBad : "LEDs not stable"));

                bool done = false;
                using (Mat frame = Grab()) {
                    if (frame == null || frame.IsEmpty) { Thread.Sleep(10); continue; }
                    using (Image<Bgr, byte> bgr = frame.ToImage<Bgr, byte>()) {
                        var count = new int[rois.Count];
                        var shown = new Rectangle[rois.Count];
                        for (int i = 0; i < rois.Count; i++) {
                            if (rois[i].IsEmpty) continue;
                            shown[i] = Clamp(rois[i], bgr.Size);
                            count[i] = CountColor(bgr, shown[i]);
                        }

                        var badOn = new List<int>(); var badOff = new List<int>();
                        foreach (int n in on)  if (count[n - 1] < _hsvMask)  badOn.Add(n);
                        foreach (int n in off) if (count[n - 1] >= _hsvMask) badOff.Add(n);
                        bool ok = badOn.Count == 0 && badOff.Count == 0;

                        if (ok) done = hold.ElapsedMilliseconds >= holdMs; else hold.Restart();
                        lastBad = (badOn.Count > 0 ? "LED not lit: " + LedSelect.Format(badOn) : "") +
                                  (badOn.Count > 0 && badOff.Count > 0 ? "; " : "") +
                                  (badOff.Count > 0 ? "LED lit but must be off: " + LedSelect.Format(badOff) : "");

                        // ── picture for the tile ──
                        var wantOn = new HashSet<int>(on); var wantOff = new HashSet<int>(off);
                        for (int i = 0; i < rois.Count; i++) {
                            if (rois[i].IsEmpty || shown[i].Width <= 0) continue;
                            int n = i + 1;
                            bool lit = count[i] >= _hsvMask;
                            MCvScalar col; int thick = 2;
                            if (wantOn.Contains(n))       col = lit ? new MCvScalar(0, 255, 0) : new MCvScalar(0, 0, 255);
                            else if (wantOff.Contains(n)) col = lit ? new MCvScalar(0, 0, 255) : new MCvScalar(0, 255, 0);
                            else { col = new MCvScalar(150, 150, 150); thick = 1; }                 // not checked
                            CvInvoke.Rectangle(bgr, shown[i], col, thick);
                            int ty = shown[i].Y > 18 ? shown[i].Y - 5 : shown[i].Bottom + 14;
                            CvInvoke.PutText(bgr, "L" + n + " " + count[i], new Point(shown[i].X, ty), FontFace.HersheySimplex, 0.5, col, 1);
                        }
                        int okCount = on.Count - badOn.Count + off.Count - badOff.Count, all = on.Count + off.Count;
                        MCvScalar head = ok ? new MCvScalar(0, 255, 0) : new MCvScalar(0, 0, 255);
                        CvInvoke.PutText(bgr, "Head " + _job.Head + " Multi-LED " + okCount + "/" + all + " ok   hold " + hold.ElapsedMilliseconds + "/" + holdMs + " ms",
                            new Point(20, 30), FontFace.HersheySimplex, 0.7, head, 2);
                        if (lastBad.Length > 0)
                            CvInvoke.PutText(bgr, lastBad, new Point(20, 60), FontFace.HersheySimplex, 0.6, new MCvScalar(0, 0, 255), 2);
                        Preview(bgr);

                        string st = "LED " + okCount + "/" + all + (ok ? " ok" : " - " + lastBad);
                        if (st != lastStatus) { lastStatus = st; Status(st); }
                    }
                }

                if (done) return Pass("LED " + LedSelect.Format(on) + " on" + (off.Count > 0 ? ", " + LedSelect.Format(off) + " off" : ""));
                Thread.Sleep(20);
            }
            return Fail("Cancelled");
        }

        /// <summary>
        /// LED n = {head}Image{n}.png exists in config\{mode}\ (a 1x1 marker written when its ROI was drawn); its box is line n of the four
        /// {head}Rectangle*.txt files. The list is as long as the highest taught LED; a number that was never taught is Rectangle.Empty.
        /// </summary>
        private List<Rectangle> BuildMultiLedRects(string dir, out string error) {
            error = null;
            string pre = dir + _job.Head;
            string[] lx = ReadLinesOrEmpty(pre + AppFilePath.RectX), ly = ReadLinesOrEmpty(pre + AppFilePath.RectY),
                     lw = ReadLinesOrEmpty(pre + AppFilePath.RectWidth), lh = ReadLinesOrEmpty(pre + AppFilePath.RectHeight);
            var list = new List<Rectangle>();
            for (int n = 1; n <= LedSelect.MaxLed; n++) {
                Rectangle r = Rectangle.Empty;
                if (File.Exists(pre + "Image" + n + ".png")) {
                    try {
                        r = new Rectangle(Convert.ToInt32(lx[n - 1]), Convert.ToInt32(ly[n - 1]), Convert.ToInt32(lw[n - 1]), Convert.ToInt32(lh[n - 1]));
                        if (r.Width <= 0 || r.Height <= 0) r = Rectangle.Empty;
                    } catch { r = Rectangle.Empty; }
                }
                while (list.Count < n - 1) list.Add(Rectangle.Empty);
                list.Add(r);
            }
            while (list.Count > 0 && list[list.Count - 1].IsEmpty) list.RemoveAt(list.Count - 1);   // nothing taught after the last LED
            return list;
        }

        private static string[] ReadLinesOrEmpty(string path) {
            try { return File.ReadAllLines(path); } catch { return new string[0]; }
        }
    }
}
