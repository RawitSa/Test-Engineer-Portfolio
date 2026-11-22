using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using ZXing;

namespace camera_show {
    public sealed partial class CameraWorker {

        /// <summary>
        /// Port of Form1.Read2dMode: crop ROI -> Canny rectangle search -> ZXing decode while rotating.
        /// On a miss the ROI is moved along the same spiral AutoScale uses. PASS = text length equals
        /// "Digit SN" (and equals the job's text when one was given).
        /// </summary>
        private JobResult RunRead2d(VideoCapture cap) {
            var total  = Stopwatch.StartNew();
            var sweep  = new RoiSweeper(_rect, _roiSweepOn, _roiMove, _roiStartMs);
            var reader = new BarcodeReader();   // one per worker

            while (!_cancel.IsCancellationRequested) {
                if (total.ElapsedMilliseconds >= _timeOutMs) return Fail("Unreadable");

                using (Mat frame = Grab()) {
                    if (frame == null || frame.IsEmpty) { Thread.Sleep(10); continue; }

                    using (Image<Bgr, byte> raw = frame.ToImage<Bgr, byte>())
                    using (Image<Bgr, byte> img = _adjustDeg == 0 ? raw.Clone() : raw.Rotate(_adjustDeg, new Bgr())) {
                        Rectangle roi = sweep.Current;
                        if (roi.Width == 0) roi = new Rectangle(roi.X, roi.Y, 100, 100);
                        roi = Clamp(roi, img.Size);

                        int boxCount;
                        string text = Decode(reader, img, roi, out boxCount);

                        CvInvoke.Rectangle(img, roi, new MCvScalar(0, 0, 255), 2);
                        CvInvoke.PutText(img, "Head " + _job.Head + " " + (text ?? "not read"),
                            new Point(20, 30), FontFace.HersheySimplex, 0.7, new MCvScalar(0, 0, 255), 2);
                        CvInvoke.PutText(img, "boxes=" + boxCount,
                            new Point(20, 60), FontFace.HersheySimplex, 0.7, new MCvScalar(0, 0, 255), 2);
                        Preview(img);

                        if (text != null && text.Length == _digitSn &&
                            (string.IsNullOrEmpty(_job.ExpectedText) ||
                             string.Equals(text, _job.ExpectedText, StringComparison.Ordinal)))
                            return Pass(text);

                        sweep.OnMiss();
                    }
                }
                Thread.Sleep(100);
            }
            return Fail("Cancelled");
        }

        /// <summary>Returns the decoded text or null. Same algorithm as the single-camera app.</summary>
        private static string Decode(BarcodeReader reader, Image<Bgr, byte> img, Rectangle roi, out int boxCount) {
            boxCount = 0;
            if (roi.Width <= 0 || roi.Height <= 0) return null;

            Image<Bgr, byte> temp = img.Copy(roi);
            try {
                // ── Canny-based rectangle detection ──
                var boxList = new List<RotatedRect>();
                using (Image<Gray, byte> imgGray = temp.Convert<Gray, byte>().ThresholdBinary(new Gray(150), new Gray(255)))
                using (var edges = new UMat())
                using (var contours = new VectorOfVectorOfPoint()) {
                    CvInvoke.Canny(imgGray, edges, 180.0, 120.0);
                    CvInvoke.FindContours(edges, contours, null, RetrType.List, ChainApproxMethod.ChainApproxSimple);
                    for (int i = 0; i < contours.Size; i++) {
                        using (var contour = contours[i])
                        using (var approx = new VectorOfPoint()) {
                            CvInvoke.ApproxPolyDP(contour, approx, CvInvoke.ArcLength(contour, true) * 0.1, true);
                            if (CvInvoke.ContourArea(approx, false) > 50 && approx.Size == 4) {
                                bool isRect = true;
                                LineSegment2D[] sides = PointCollection.PolyLine(approx.ToArray(), true);
                                for (int j = 0; j < sides.Length; j++) {
                                    double angle = Math.Abs(sides[(j + 1) % sides.Length].GetExteriorAngleDegree(sides[j]));
                                    if (angle < 70 || angle > 100) { isRect = false; break; }
                                }
                                if (isRect) boxList.Add(CvInvoke.MinAreaRect(approx));
                            }
                        }
                    }
                }
                boxCount = boxList.Count;

                // ── Decode, rotating the whole crop in 5-degree steps ──
                Result result = null;
                var rot = new Bgr();
                for (int i = 1; i <= 17; i++) {
                    result = reader.Decode(temp.Bitmap);
                    if (result != null) break;
                    Image<Bgr, byte> prev = temp;
                    temp = temp.Rotate(5, rot);
                    prev.Dispose();
                }

                // ── Fallback: decode each detected rectangle ──
                if (result == null) {
                    foreach (RotatedRect box in boxList) {
                        Rectangle brect = Rectangle.Intersect(
                            CvInvoke.BoundingRectangle(new VectorOfPointF(box.GetVertices())),
                            new Rectangle(Point.Empty, temp.Size));
                        if (brect.Width <= 0 || brect.Height <= 0) continue;

                        Image<Bgr, byte> tempBox = temp.Copy(brect);
                        for (int i = 1; i <= 17; i++) {
                            result = reader.Decode(tempBox.Bitmap);
                            if (result != null) break;
                            Image<Bgr, byte> prev = tempBox;
                            tempBox = tempBox.Rotate(i * 5, rot);
                            prev.Dispose();
                            result = reader.Decode(tempBox.Bitmap);
                            if (result != null) break;
                        }
                        tempBox.Dispose();
                        if (result != null) break;
                    }
                }
                return result == null ? null : result.Text;
            } finally {
                temp.Dispose();
            }
        }

        /// <summary>Per-worker copy of AutoScale's ROI spiral (AutoScale itself is tied to Form1).</summary>
        private sealed class RoiSweeper {
            private readonly Rectangle _base;
            private readonly bool _enabled;
            private readonly int _move, _startMs;
            private readonly Stopwatch _sw = new Stopwatch();
            private int _index, _stage;
            private bool _started;

            // Own copy of AutoScale's ROI spiral (offsets in units of "Roi Move" pixels), so this
            // class does not depend on AutoScale's member visibility. 80 positions.
            private static readonly int[] SpiralX = {
            -1, 0, 1, -1, 1, -1, 0, 1, -2, -1, 0, 1, 2, -2, -1, 0, 1, 2, -2, -1,
            1, 2, -2, -1, 0, 1, 2, -2, -1, 0, 1, 2, -3, -2, -1, 0, 1, 2, 3, -3,
            -2, -1, 0, 1, 2, 3, -3, -2, -1, 0, 1, 2, 3, -3, -2, -1, 1, 2, 3, -3,
            -2, -1, 0, 1, 2, 3, -3, -2, -1, 0, 1, 2, 3, -3, -2, -1, 0, 1, 2, 3 };
            private static readonly int[] SpiralY = {
            -1, -1, -1, 0, 0, 1, 1, 1, -2, -2, -2, -2, -2, -1, -1, -1, -1, -1, 0, 0,
            0, 0, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, -3, -3, -3, -3, -3, -3, -3, -2,
            -2, -2, -2, -2, -2, -2, -1, -1, -1, -1, -1, -1, -1, 0, 0, 0, 0, 0, 0, 1,
            1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3 };

            public Rectangle Current { get; private set; }

            public RoiSweeper(Rectangle baseRect, bool enabled, int move, int startMs) {
                _base = Current = baseRect; _enabled = enabled; _move = move; _startMs = startMs;
            }

            /// <summary>Call after every failed decode, exactly like AutoScale.Run.</summary>
            public void OnMiss() {
                if (!_started) { _started = true; _sw.Restart(); }
                if (_stage == 0) {
                    if (_sw.ElapsedMilliseconds > _startMs) { _sw.Stop(); _stage = 1; }
                    return;
                }
                if (!_enabled) return;
                if (_move == 0 || _index >= SpiralX.Length) { Current = _base; _index = 0; return; }
                Current = new Rectangle(
                    _base.X + SpiralX[_index] * _move,
                    _base.Y + SpiralY[_index] * _move,
                    _base.Width, _base.Height);
                _index++;
            }
        }
    }
}
