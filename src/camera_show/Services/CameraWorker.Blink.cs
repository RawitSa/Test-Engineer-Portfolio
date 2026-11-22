using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;

namespace camera_show {
    public sealed partial class CameraWorker {

        /// <summary>
        /// Port of Form1.BlinkLedMode. The LED on/off times are measured between frames, so the result
        /// depends on the camera frame rate: there is deliberately no Sleep in this loop.
        /// </summary>
        private JobResult RunBlink(VideoCapture cap) {
            var total = Stopwatch.StartNew();
            var bl = new BlinkLedData();      // one measurement state per worker

            while (!_cancel.IsCancellationRequested) {
                if (total.ElapsedMilliseconds >= _timeOutMs) return Fail("(1)time over");

                using (Mat frame = Grab()) {
                    if (frame == null || frame.IsEmpty) { Thread.Sleep(5); continue; }
                    using (Image<Bgr, byte> bgr = frame.ToImage<Bgr, byte>()) {
                        Rectangle roi = Clamp(_rect, bgr.Size);
                        int pixels = CountColor(bgr, roi);
                        bool mask = pixels >= _hsvMask;

                        if (mask) {
                            double period = bl.TimeLow + bl.TimeHigh;
                            if (bl.TricCal && period > 0) {
                                int countSup  = (int)(2000.0 / period);
                                bl.CounterMax = (int)Map(countSup, 0, 2000, 1, 1200);
                                bl.TricCal    = false;
                                bl.FrequencySup += 1.0 / (period / 1000.0);
                                bl.DutySup      += bl.TimeHigh * 100.0 / period;
                                bl.Counter++;

                                if (bl.Counter >= bl.CounterMax) {
                                    bl.Frequency = bl.FrequencySup / bl.Counter;
                                    bl.Duty      = bl.DutySup / bl.Counter;
                                    bl.FindResult();
                                    bl.Counter = 0; bl.FrequencySup = 0; bl.DutySup = 0;
                                }
                            }
                            bl.TimeHigh = bl.StopWatchHigh.ElapsedMilliseconds;
                            bl.StopWatchLow.Restart();
                        } else {
                            bl.TimeLow = bl.StopWatchLow.ElapsedMilliseconds;
                            bl.StopWatchHigh.Restart();
                            bl.TricCal = true;
                        }

                        CvInvoke.Rectangle(bgr, roi, new MCvScalar(0, 0, 255), 2);
                        CvInvoke.PutText(bgr, "Head " + _job.Head + " pixels=" + pixels + " on=" + mask,
                            new Point(20, 30), FontFace.HersheySimplex, 0.7, new MCvScalar(0, 0, 255), 2);
                        CvInvoke.PutText(bgr, "Freq=" + bl.Frequency.ToString("0.0000") + " Duty=" + bl.Duty.ToString("0.00"),
                            new Point(20, 60), FontFace.HersheySimplex, 0.7, new MCvScalar(0, 0, 255), 2);
                        Preview(bgr);
                    }
                }

                if (bl.FlagResult) return Pass(bl.Result.ToString("0.0000"));
            }
            return Fail("Cancelled");
        }

        private static float Map(float value, float from1, float to1, float from2, float to2) =>
            (value - from1) / (to1 - from1) * (to2 - from2) + from2;
    }
}
