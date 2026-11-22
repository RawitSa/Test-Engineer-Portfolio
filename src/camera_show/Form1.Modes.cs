//----------------------------------------------------------------------------
// Form1 (partial) — Application.Idle mode handlers
// Each mode is registered in CheckMode() (Form1.Camera.cs).
//----------------------------------------------------------------------------
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Face;
using Emgu.CV.Ocl;
using Emgu.CV.OCR;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using Spire.Xls.Core.Spreadsheet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using ZXing;

namespace camera_show {
    public partial class Form1 {

        // ── Helper: handle a null / dead capture inside a mode handler ────────
        private bool HandleNullCapture() {
            ReopenCamera();
            if (IsCaptureValid()) {
                this.ClientSize  = new Size(setCamera.Capture.Width, setCamera.Capture.Height);
                pictureBox1.Size = new Size(setCamera.Capture.Width, setCamera.Capture.Height);
                pictureBox1.Location = new Point(0, 0);
                ReadConfigCamera();
            }
            return true; // caller should return after this
        }

        // ── Frame display helper ──────────────────────────────────────────────

        /// <summary>
        /// Routes the PictureBox to the correct frame.
        /// While snapshot is active the frozen copy is shown; otherwise the live frame is shown.
        /// </summary>
        private void ShowFrame() {
            pictureBox1.Image = flag.Snapshot ? _snapshotImage?.Bitmap : global.Image?.Bitmap;
        }

        // ── Normal mode (display only, no recognition) ────────────────────────
        private void NormalMode(object sender, EventArgs e) {
            if (_isMouseDown) return;
            if (!IsCaptureValid()) { HandleNullCapture(); return; }


            try {
                using (Mat frame = setCamera.Capture.QueryFrame()) {
                    if (IsBlackFrame(frame)) { HandleBlackFrame(); return; }
                    _consecutiveBlackFrames = 0;

                    var old = global.UpdateImage(frame.ToImage<Bgr, byte>());
                    ShowFrame();
                    old?.Dispose();
                }
            } catch {
                MessageBox.Show(AppText.CannotOpenCamera);
                flag.CloseForm = true;
                Application.Exit();
            }

            Thread.Sleep(100);
        }

        // ── Barcode / QR reader mode ──────────────────────────────────────────
        private void Read2dMode(object sender, EventArgs e) {
            if (flag.ClearStopWatch) {
                flag.ClearStopWatch = false;
                global.StopWatch.Restart();
            }

            if (global.StopWatch.ElapsedMilliseconds >= global.TimeOut) {
                StampFailRead2d();
                if (!flag.Debug) return;
            }

            if (_isMouseDown) return;

            if (!IsCaptureValid()) {
                HandleNullCapture();
                flag.ClearStopWatch = true;
                return;
            }

            // ── Grab frame ───────────────────────────────────────────────────
            try {
                // Flush frames that accumulated in the camera buffer during the
                // previous decode pass so we always process the latest image.

                //for (int flush = 0; flush < 3; flush++) {
                //    using (var dummy = setCamera.Capture.QueryFrame()) {
                //        if (dummy == null || dummy.IsEmpty) break;
                //    }
                //}
                ////Remark***** flush flame ทิ้งเยอะเกินไปทำให้ CV capture frame มาไม่ทันทำให้บัค


                //using (Mat frame = setCamera.Capture.QueryFrame()) {
                //    if (IsBlackFrame(frame)) { HandleBlackFrame(); return; }
                //    _consecutiveBlackFrames = 0;
                //    if (frame == null)
                //    {
                //        Debug.WriteLine("frame null");
                //        return;
                //    }
                //    Debug.WriteLine($"empty={frame.IsEmpty}, size={frame.Width}x{frame.Height}, channels={frame.NumberOfChannels}");
                //    var mean = CvInvoke.Mean(frame);
                //    Debug.WriteLine($"mean={mean.V0},{mean.V1},{mean.V2}, sum={mean.V0 + mean.V1 + mean.V2}");
                //    var raw = frame.ToImage<Bgr, byte>();
                //    var old = global.UpdateImage(raw.Rotate(global.AdjustDegree, new Bgr()));
                //    raw.Dispose();
                //    ShowFrame(); // updated later after overlay
                //    old?.Dispose();
                //}
                ////Remark***** capture frame แล้วรูปดำหรือรูปขาวพอเอาภาพไปเทียบกับ threshold ที่กำหนดไว้ทำให้โปรแกรมทำการ recap ใหม่แต่ว่าวนลูปการทำงานไวเกินไปทำให้รูปที่นำมาโชว์ที่โปรแกรมwม่เจอภาพ


                ////Remark****** ไม่ทำการ flush รูปทิ้งและไม่ทำการวนอ่านรูปถ้าเจอกำหรือขาว แต่ให้ทำการนำรูปนั้นมาใช้งานเลยโดยจะไปทำการเปรียบเทียบว่ารูปเป็นขาวหรือดำทีหลัง 
                ////ถ้ารูปเป็นขาวหรือดำค่อยทำการวนไปอ่านรูปใหม่
                using (Mat frame = setCamera.Capture.QueryFrame())
                {
                    if (frame == null || frame.IsEmpty)
                    {
                        Debug.WriteLine("frame null/empty");
                        HandleBlackFrame();
                        return;
                    }

                    var mean = CvInvoke.Mean(frame);
                    var sum = mean.V0 + mean.V1 + mean.V2;
                    Debug.WriteLine($"mean={mean.V0},{mean.V1},{mean.V2}, sum={sum}");

                    using (var raw = frame.ToImage<Bgr, byte>())
                    using (var rotated = raw.Rotate(global.AdjustDegree, new Bgr()))
                    {
                        var old = global.UpdateImage(rotated.Clone());
                        ShowFrame();
                        old?.Dispose();
                    }

                    if (sum < BlackFrameMeanThreshold)
                    {
                        Debug.WriteLine($"black frame detected after display: sum={sum}, threshold={BlackFrameMeanThreshold}");
                        HandleBlackFrame();
                    }
                    else
                    {
                        _consecutiveBlackFrames = 0;
                    }
                }
            } catch {
                MessageBox.Show(AppText.CannotOpenCamera);
                Application.Exit();
                return;
            }

            // ── Draw ROI ─────────────────────────────────────────────────────
            using (Graphics g = Graphics.FromImage(global.Image.Bitmap))
                g.DrawRectangle(Pens.Red, Rect);

            // ── Crop to ROI ──────────────────────────────────────────────────
            var imageCut = global.Image.Copy();
            var r = Rect.Width == 0 ? new Rectangle(Rect.X, Rect.Y, 100, 100) : Rect;
            try { imageCut.ROI = r; } catch { }
            var temp = imageCut.Copy();
            imageCut.Dispose();

            // ── Canny-based rectangle detection ──────────────────────────────
            var boxList   = new List<RotatedRect>();
            var imgGray   = temp.Convert<Gray, byte>().ThresholdBinary(new Gray(150), new Gray(255));
            using (var edges = new UMat()) {
                CvInvoke.Canny(imgGray, edges, 180.0, 120.0);
                using (var contours = new VectorOfVectorOfPoint()) {
                    CvInvoke.FindContours(edges, contours, null, RetrType.List, ChainApproxMethod.ChainApproxSimple);
                    for (int i = 0; i < contours.Size; i++) {
                        using (var contour = contours[i])
                        using (var approx  = new VectorOfPoint()) {
                            CvInvoke.ApproxPolyDP(contour, approx,
                                CvInvoke.ArcLength(contour, true) * 0.1, true);
                            if (CvInvoke.ContourArea(approx, false) > 50 && approx.Size == 4) {
                                bool isRect = true;
                                var pts = approx.ToArray();
                                var edges2 = PointCollection.PolyLine(pts, true);
                                for (int j = 0; j < edges2.Length; j++) {
                                    double angle = Math.Abs(edges2[(j + 1) % edges2.Length]
                                                   .GetExteriorAngleDegree(edges2[j]));
                                    if (angle < 70 || angle > 100) { isRect = false; break; }
                                }
                                if (isRect) boxList.Add(CvInvoke.MinAreaRect(approx));
                            }
                        }
                    }
                }
            }
            imgGray.Dispose();

            // ── Barcode decode (rotating) ─────────────────────────────────────
            var    reader = new BarcodeReader();
            Result result = null;
            var    rot    = new Bgr();
            for (int i = 1; i <= 17; i++) {
                result = reader.Decode(temp.Bitmap);
                if (result != null) break;
                var prevTemp = temp;
                temp = temp.Rotate(5, rot);
                prevTemp.Dispose();
            }

            if (result == null) {
                foreach (RotatedRect box in boxList) {
                    var brect = CvInvoke.BoundingRectangle(new VectorOfPointF(box.GetVertices()));
                    var crop2 = temp.Copy();
                    crop2.ROI = brect;
                    var tempBox = crop2.Copy();
                    crop2.Dispose();
                    for (int i = 1; i <= 17; i++) {
                        result = reader.Decode(tempBox.Bitmap);
                        if (result != null) break;
                        var prevBox = tempBox;
                        tempBox = tempBox.Rotate(i * 5, rot);
                        prevBox.Dispose();
                        result  = reader.Decode(tempBox.Bitmap);
                    }
                    tempBox.Dispose();
                    if (result != null) break;
                }
            }
            temp.Dispose();

            // ── Overlay result ────────────────────────────────────────────────
            if (result != null) {
                CvInvoke.PutText(global.Image, result.Text,
                    new Point(20, 30), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);

                if (result.Text.Length == global.DigitSn) {
                    if (!setCamera.FlagOpen && !flag.Debug) {
                        File.WriteAllText(setPath.ResultTxt, result + "\r\nPASS");
                        this.Close();
                    }
                    flag.ResultPass = true;
                    global.ResultBackup = result.ToString();
                } else {
                    if (!setCamera.FlagOpen) autoScale.Run(this);
                    flag.ResultPass = false;
                }
            } else {
                CvInvoke.PutText(global.Image, "not read",
                    new Point(20, 30), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
                if (!setCamera.FlagOpen) autoScale.Run(this);
                flag.ResultPass = false;
            }

            CvInvoke.PutText(global.Image, boxList.Count.ToString(),
                new Point(20, 60), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, "Port = " + global.Port,
                new Point(20, 90), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            ShowFrame();
            Thread.Sleep(100);
        }

        // ── LED colour-check mode ─────────────────────────────────────────────
        private void CheckLedMode(object sender, EventArgs e) {
            if (flag.ClearStopWatch) {
                flag.ClearStopWatch = false;
                global.StopWatch.Restart();
            }

            if (global.StopWatch.ElapsedMilliseconds >= global.TimeOut) {
                StampFailCheckLed();
                if (!flag.Debug) return;
            }

            if (_isMouseDown) return;

            //if (!IsCaptureValid()) {
            //    HandleNullCapture();
            //    flag.ClearStopWatch = true;
            //    return;
            //}

            try {
                using (Mat frame = setCamera.HsvTestFlag
                    ? new Mat(AppPaths.ConfigDir + "hsv_test.png")
                    : setCamera.Capture.QueryFrame()) {
                    var oldBgr = global.UpdateImage(frame.ToImage<Bgr, byte>());
                    var oldHsv = global.UpdateImageHsv(frame.ToImage<Hsv, byte>());
                    oldBgr?.Dispose();
                    oldHsv?.Dispose();
                }
            } catch {
                MessageBox.Show(AppText.CannotOpenCamera);
                Application.Exit();
                return;
            }

            using (Graphics g = Graphics.FromImage(global.Image.Bitmap))
                g.DrawRectangle(Pens.Red, Rect);

            int redpixels = 0;
            if (!setCamera.HsvFlag) {
                var cut = global.Image.Copy();
                try { cut.ROI = Rect; } catch { }
                var sup = cut.Copy();
                try { redpixels = sup.InRange(setCamera.BgrLow, setCamera.BgrHigh).CountNonzero()[0]; } catch { }
            } else {
                var cutHsv = global.ImageHsv.Copy();
                try { cutHsv.ROI = Rect; } catch { }
                var supHsv = cutHsv.Copy();
                try { redpixels = supHsv.InRange(setCamera.HsvLow, setCamera.HsvHigh).CountNonzero()[0]; } catch { }
            }

            bool mask = false;
            if (redpixels >= setCamera.HsvMask) {
                if (global.StopWatchHsv.ElapsedMilliseconds >= setCamera.HsvTimeout)
                    mask = true;
            } else {
                global.StopWatchHsv.Restart();
            }

            CvInvoke.PutText(global.Image, redpixels.ToString(),
                new Point(20, 30), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, mask.ToString(),
                new Point(20, 60), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, global.StopWatchHsv.ElapsedMilliseconds.ToString(),
                new Point(pictureBox1.Width - 100, 30), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, "Port = " + global.Port,
                new Point(20, 90), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            ShowFrame();

            if (mask) {
                if (!setCamera.FlagOpen && !flag.Debug) {
                    GetDebug();
                    global.NumberCheckLed++;
                    ctms_checkLedNumber.Text = global.NumberCheckLed.ToString();
                    if (File.Exists(setPath.FolderCheckLed + global.Head + "Image" + global.NumberCheckLed + ".png")) {
                        var rx = File.ReadAllLines(setPath.FolderCheckLed + global.Head + AppFilePath.RectX).ToList();
                        var ry = File.ReadAllLines(setPath.FolderCheckLed + global.Head + AppFilePath.RectY).ToList();
                        var rw = File.ReadAllLines(setPath.FolderCheckLed + global.Head + AppFilePath.RectWidth).ToList();
                        var rh = File.ReadAllLines(setPath.FolderCheckLed + global.Head + AppFilePath.RectHeight).ToList();
                        try {
                            var nr = Rect;
                            nr.X      = Convert.ToInt32(rx[global.NumberCheckLed - 1]);
                            nr.Y      = Convert.ToInt32(ry[global.NumberCheckLed - 1]);
                            nr.Width  = Convert.ToInt32(rw[global.NumberCheckLed - 1]);
                            nr.Height = Convert.ToInt32(rh[global.NumberCheckLed - 1]);
                            Form1.Rect = nr;
                        } catch { }
                        global.NumStep++;
                        global.StopWatchHsv.Restart();
                        return;
                    }

                    Thread.Sleep(50);
                    File.WriteAllText(setPath.ResultTxt, "Color Detected\r\nPASS");
                    this.Close();
                }
                flag.ResultPass    = true;
                global.ResultBackup = "Color Detected";
            } else {
                reAdjust.Run(this);
                flag.ResultPass = false;
                editValueCamDiv3.Process();
            }

            Thread.Sleep(100);
        }

        // ── Blink-LED frequency-measurement mode ──────────────────────────────
        private void BlinkLedMode(object sender, EventArgs e) {
            if (flag.ClearStopWatch) {
                flag.ClearStopWatch = false;
                global.StopWatch.Restart();
            }

            if (global.StopWatch.ElapsedMilliseconds >= global.TimeOut) {
                StampFailBlinkLed();
                if (!flag.Debug) return;
            }

            if (_isMouseDown) return;

            if (!IsCaptureValid()) {
                HandleNullCapture();
                flag.ClearStopWatch = true;
                return;
            }

            try {
                using (Mat frame = setCamera.HsvTestFlag
                    ? new Mat(AppPaths.ConfigDir + "hsv_test.png")
                    : setCamera.Capture.QueryFrame()) {
                    var oldBgr = global.UpdateImage(frame.ToImage<Bgr, byte>());
                    var oldHsv = global.UpdateImageHsv(frame.ToImage<Hsv, byte>());
                    oldBgr?.Dispose();
                    oldHsv?.Dispose();
                }
            } catch {
                MessageBox.Show(AppText.CannotOpenCamera);
                Application.Exit();
                return;
            }

            using (Graphics g = Graphics.FromImage(global.Image.Bitmap))
                g.DrawRectangle(Pens.Red, Rect);

            int redpixels = 0;
            if (!setCamera.HsvFlag) {
                var cut = global.Image.Copy();
                try { cut.ROI = Rect; } catch { }
                var sup = cut.Copy();
                try { redpixels = sup.InRange(setCamera.BgrLow, setCamera.BgrHigh).CountNonzero()[0]; } catch { }
            } else {
                var cutHsv = global.ImageHsv.Copy();
                try { cutHsv.ROI = Rect; } catch { }
                var supHsv = cutHsv.Copy();
                try { redpixels = supHsv.InRange(setCamera.HsvLow, setCamera.HsvHigh).CountNonzero()[0]; } catch { }
            }

            bool mask = redpixels >= setCamera.HsvMask;
            var  bl   = global.BlinkLed;

            if (mask) {
                if (bl.TricCal) {
                    int countSup    = (int)(2000.0 / (bl.TimeLow + bl.TimeHigh));
                    bl.CounterMax   = (int)Map(countSup, 0, 2000, 1, 1200);
                    bl.TricCal      = false;
                    bl.FrequencySup += 1.0 / ((bl.TimeLow + bl.TimeHigh) / 1000.0);
                    bl.DutySup      += bl.TimeHigh * 100.0 / (bl.TimeLow + bl.TimeHigh);
                    bl.Counter++;

                    if (bl.Counter >= bl.CounterMax) {
                        bl.Frequency    = bl.FrequencySup / bl.Counter;
                        bl.Duty         = bl.DutySup      / bl.Counter;
                        bl.FindResult();
                        bl.Counter      = 0;
                        bl.FrequencySup = 0;
                        bl.DutySup      = 0;
                    }
                }
                bl.TimeHigh = bl.StopWatchHigh.ElapsedMilliseconds;
                bl.StopWatchLow.Restart();
            } else {
                bl.TimeLow  = bl.StopWatchLow.ElapsedMilliseconds;
                bl.StopWatchHigh.Restart();
                bl.TricCal  = true;
            }

            CvInvoke.PutText(global.Image, redpixels.ToString(),
                new Point(20, 30), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, mask.ToString(),
                new Point(20, 60), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, "Frequency=" + bl.Frequency.ToString("0.0000"),
                new Point(pictureBox1.Width - 200, 30), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, "Duty=" + bl.Duty.ToString("0.0000"),
                new Point(pictureBox1.Width - 200, 60), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, "Port = " + global.Port,
                new Point(20, 90), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            ShowFrame();

            if (!setCamera.FlagOpen && !flag.Debug) {
                if (bl.FlagResult) {
                    Thread.Sleep(50);
                    File.WriteAllText(setPath.ResultTxt, bl.Result.ToString("0.0000") + "\r\nPASS");
                    this.Close();
                }
            }
        }

        // ── Multiple LED colour-check mode ─────────────────────────────────────────────
        private void MultipleLed(object sendor, EventArgs e)
        {
            if (!flag_first_test)
            {
                flag_first_test = true;
                this.Activate();
                if (Form.ActiveForm != this)
                {
                    this.WindowState = FormWindowState.Minimized;
                    this.WindowState = FormWindowState.Normal;
                }
            }
            if (flag_return) { flag_return = false; global.StopWatch.Restart(); }
            if (flag.ClearStopWatch)
            {
                flag.ClearStopWatch = false;
                global.StopWatch.Restart();
            }

            if (global.StopWatch.ElapsedMilliseconds >= global.TimeOut)
            {
                StampFailMultipleLed();
                if (!flag.Debug) return;
            }

            if (_isMouseDown) return;

            //if (!IsCaptureValid())
            //{
            //    HandleNullCapture();
            //    flag.ClearStopWatch = true;
            //    return;
            //}

            try
            {
                for (int flush = 0; flush < 2; flush++)
                {
                    using (var dummy = setCamera.Capture.QueryFrame())
                    {
                        if (dummy == null || dummy.IsEmpty) break;
                    }
                    Thread.Sleep(200);
                } // flush frame 3 frame before read data 

                using (Mat frame = setCamera.HsvTestFlag
                    ? new Mat(AppPaths.ConfigDir + "hsv_test.png")
                    : setCamera.Capture.QueryFrame())
                {
                    var oldBgr = global.UpdateImage(frame.ToImage<Bgr, byte>());
                    var oldHsv = global.UpdateImageHsv(frame.ToImage<Hsv, byte>());
                    oldBgr?.Dispose();
                    oldHsv?.Dispose();
                }
            }
            catch
            {
                MessageBox.Show(AppText.CannotOpenCamera);
                Application.Exit();
                return;
            }

            int redpixels = 0;
            if (flag_led_test == 1 || flag_led_test == 4)
            {//แดง
                Graphics g = Graphics.FromImage(global.Image.Bitmap);
                g.DrawRectangle(Pens.Red, Rect);
                //Image<Hsv, byte> img_cut2 = null;
                //Image<Hsv, byte> img2 = null;
                //img_cut2 = global.ImageHsv.Copy();
                //try { img_cut2.ROI = Rect; } catch { }
                //img2 = img_cut2.Copy();
                //try { redpixels = img2.InRange(setCamera.HsvLow, setCamera.HsvHigh).CountNonzero()[0]; } catch { }
                Image<Bgr, byte> img_cut = null;
                Image<Bgr, byte> img1 = null;
                img_cut = global.Image.Copy();
                try { img_cut.ROI = Rect; } catch { }
                img1 = img_cut.Copy();
                try { redpixels = img1.InRange(setCamera.BgrLow, setCamera.BgrHigh).CountNonzero()[0]; } catch { }
            }
            else
            {//เขียว //น้ำเงิน
                Graphics g = Graphics.FromImage(global.Image.Bitmap);
                if (flag_led_test == 2 || flag_led_test == 5) 
                    g.DrawRectangle(Pens.Lime, Rect);
                else 
                    g.DrawRectangle(Pens.Blue, Rect);
                Image<Bgr, byte> img_cut = null;
                Image<Bgr, byte> img1 = null;
                img_cut = global.Image.Copy();
                try { img_cut.ROI = Rect; } catch { }
                img1 = img_cut.Copy();
                try { redpixels = img1.InRange(setCamera.BgrLow, setCamera.BgrHigh).CountNonzero()[0]; } catch { }
            }
            bool mask = false;
            if (redpixels >= setCamera.HsvMask)
            {
                if (global.StopWatchHsv.ElapsedMilliseconds >= setCamera.HsvTimeout)
                    mask = true;
            }
            else
            {
                global.StopWatchHsv.Restart();
                mask = false;
            }

            CvInvoke.PutText(global.Image, redpixels.ToString(),
                new Point(20, 30), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, mask.ToString(),
                new Point(20, 60), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, global.StopWatchHsv.ElapsedMilliseconds.ToString(),
                new Point(pictureBox1.Width - 100, 30), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, "Port = " + global.Port,
                new Point(20, 90), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            ShowFrame();

            if (mask)
            {
                if (!setCamera.FlagOpen && !flag.Debug)
                {
                    GetDebug();
                    global.NumberCheckLed++;
                    ctms_checkLedNumber.Text = global.NumberCheckLed.ToString();
                    if (File.Exists(setPath.FolderMultipleLed + global.Head + "Image" + global.NumberCheckLed + ".png"))
                    {
                        var rx = File.ReadAllLines(setPath.FolderMultipleLed + global.Head + AppFilePath.RectX).ToList();
                        var ry = File.ReadAllLines(setPath.FolderMultipleLed + global.Head + AppFilePath.RectY).ToList();
                        var rw = File.ReadAllLines(setPath.FolderMultipleLed + global.Head + AppFilePath.RectWidth).ToList();
                        var rh = File.ReadAllLines(setPath.FolderMultipleLed + global.Head + AppFilePath.RectHeight).ToList();
                        try
                        {
                            var nr = Rect;
                            nr.X = Convert.ToInt32(rx[global.NumberCheckLed - 1]);
                            nr.Y = Convert.ToInt32(ry[global.NumberCheckLed - 1]);
                            nr.Width = Convert.ToInt32(rw[global.NumberCheckLed - 1]);
                            nr.Height = Convert.ToInt32(rh[global.NumberCheckLed - 1]);
                            Form1.Rect = nr;
                        }
                        catch { }
                        global.NumStep++;
                        global.StopWatchHsv.Restart();
                        return;
                    }

                    Thread.Sleep(50);
                    File.WriteAllText(setPath.ResultTxt, "Color Detected\r\nPASS");
                    this.Close();
                }
                flag.ResultPass = true;
                global.ResultBackup = "Color Detected";
            }
            else
            {
                //reAdjust.Run(this);
                flag.ResultPass = false;
                //editValueCamDiv3.Process();
            }

            Thread.Sleep(100);
        }

        // ── OCR check mode ─────────────────────────────────────────────
        // private List<OcrCheckItem> _ocrItems;
        private int _ocrIndex;
        private Emgu.CV.OCR.Tesseract _ocrEngine;
        private void CheckOCR(object sendor, EventArgs e)
        {
            if (!flag_first_test)
            {
                flag_first_test = true;
                this.Activate();
                if (Form.ActiveForm != this)
                {
                    this.WindowState = FormWindowState.Minimized;
                    this.WindowState = FormWindowState.Normal;
                }
            }
            if (flag_return) { flag_return = false; global.StopWatch.Restart(); }
            if (flag.ClearStopWatch)
            {
                flag.ClearStopWatch = false;
                global.StopWatch.Restart();
            }

            if (global.StopWatch.ElapsedMilliseconds >= global.TimeOut)
            {
                StampFailOcr();
                if (!flag.Debug) return;
            }

            if (_isMouseDown) return;

            //if (!IsCaptureValid())
            //{
            //    HandleNullCapture();
            //    flag.ClearStopWatch = true;
            //    return;
            //}

            //try
            //{
            //    using (Mat frameEmpty = setCamera.Capture.QueryFrame())
            //    {
            //        var oldEmpty = global.UpdateImage(frameEmpty.ToImage<Bgr, byte>());
            //        oldEmpty?.Dispose();
            //    }
            //    CvInvoke.PutText(global.Image, "No OCR settings found: " + setPath.FolderCheckOCR + global.Head + AppFilePath.OcrSettings,
            //        new Point(20, 30), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            //    ShowFrame();
            //}
            //catch { }
            try
            {
                //for (int flush = 0; flush < 2; flush++)
                //{
                //    using (var dummy = setCamera.Capture.QueryFrame())
                //    {
                //        if (dummy == null || dummy.IsEmpty) break;
                //    }
                //    DelaymS(200);
                //}
                //Remark***** flush flame ทิ้งเยอะเกินไปทำให้ CV capture frame มาไม่ทันทำให้บัค

                using (Mat frame = setCamera.HsvTestFlag
                    ? new Mat(AppPaths.ConfigDir + "hsv_test.png")
                    : setCamera.Capture.QueryFrame())
                {
                    var oldHsv = global.UpdateImageHsv(frame.ToImage<Hsv, byte>());
                    using (var bgrRaw = frame.ToImage<Bgr, byte>())
                    {
                        // หมุนทั้งเฟรม 180 องศาครั้งเดียวตรงนี้ — ทุกอย่างต่อจากนี้ถูกทางหมด
                        var rotatedImage = bgrRaw.Rotate(180, new Bgr(0, 0, 0));
                        var oldBgr = global.UpdateImage(rotatedImage);
                        oldBgr?.Dispose();
                        oldHsv?.Dispose();
                    }
                }
            }
            catch
            {
                MessageBox.Show(AppText.CannotOpenCamera);
                Application.Exit();
                return;
            }

            var imageCut = global.Image.Copy();
            imageCut.ROI = Rect;

            using (Graphics g = Graphics.FromImage(global.Image.Bitmap))
                g.DrawRectangle(Pens.Yellow, Rect);

            string readText = ReadOcrText(Rect, imageCut);

            bool passThis = string.Equals(readText, global.textocr, StringComparison.Ordinal);

            CvInvoke.PutText(global.Image, "Read: " + readText,
                new Point(20, 30), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            CvInvoke.PutText(global.Image, "Port = " + global.Port,
                new Point(20, 60), FontFace.HersheySimplex, 0.5, new MCvScalar(0, 0, 255), 2);
            ShowFrame();

            if (passThis)
            {
                global.StopWatchHsv.Restart();

                // All regions passed.
                if (!setCamera.FlagOpen && !flag.Debug)
                {
                    Thread.Sleep(50);
                    File.WriteAllText(setPath.ResultTxt, $"{readText}\r\n{passThis}");
                    this.Close();
                }
                flag.ResultPass = true;
                global.ResultBackup = "OCR Detected";
            }
            else
            {
                //reAdjust.Run(this);
                flag.ResultPass = false;
                //editValueCamDiv3.Process();
            }
            //DelaymS(100);
            Thread.Sleep(100);
        }
        private string ReadOcrText(Rectangle roi, Image<Bgr, byte> sourceImage)
        {
            try
            {
                //Rectangle paddedRoi = Rectangle.Inflate(roi, 8, 8);
                //paddedRoi.Intersect(new Rectangle(Point.Empty, sourceImage.Size));
                using (var cut = sourceImage.Copy())
                {
                    //cut.ROI = paddedRoi;
                    using (var cropped = cut.Copy())
                    using (var gray = cropped.Convert<Gray, byte>())
                    {
                        CvInvoke.GaussianBlur(gray, gray, new Size(5, 5), 0);

                        // Otsu แทนค่าคงที่ 128 — ทนต่อแสงไม่สม่ำเสมอกว่า
                        Mat otsuMat = new Mat();
                        CvInvoke.Threshold(gray.Mat, otsuMat, 0, 255,
                            ThresholdType.Otsu | ThresholdType.Binary);

                        // invert ให้เป็นตัวดำบนพื้นขาว (ขั้วที่ Tesseract แม่นสุด)
                        Mat invMat = new Mat();
                        CvInvoke.BitwiseNot(otsuMat, invMat);

                        //using (var flipped = new Mat())
                        //{
                        //    //CvInvoke.Rotate(invMat, flipped, RotateFlags.Rotate180);

                        //    // upscale 3x ให้ตัวอักษรสูงพอ Tesseract อ่านแม่น
                        //    Mat upscaled = new Mat();
                        //    CvInvoke.Resize(flipped, upscaled,
                        //        new Size(flipped.Cols * 3, flipped.Rows * 3), 0, 0, Inter.Cubic);
                        //    CvInvoke.Threshold(upscaled, upscaled, 127, 255, ThresholdType.Binary);

                        //    using (var finalImg = upscaled.ToImage<Gray, byte>())
                        //    {
                        //        return RunTesseract(finalImg);
                        //    }
                        //}
                        Mat upscaled = new Mat();
                        CvInvoke.Resize(invMat, upscaled,
                            new Size(invMat.Cols * 3, invMat.Rows * 3), 0, 0, Inter.Cubic);
                        CvInvoke.Threshold(upscaled, upscaled, 127, 255, ThresholdType.Binary);

                        using (var finalImg = upscaled.ToImage<Gray, byte>())
                        {
                            return RunTesseract(finalImg);
                        }
                    }
                }
            }
            catch { return string.Empty; }
        }
        private Emgu.CV.OCR.Tesseract GetOcrEngine()
        {
            if (_ocrEngine == null)
            {
                _ocrEngine = new Emgu.CV.OCR.Tesseract(AppFilePath.TessData, "eng", Emgu.CV.OCR.OcrEngineMode.TesseractLstmCombined);
                _ocrEngine.SetVariable("preserve_interword_spaces", "1");
                _ocrEngine.SetVariable("tessedit_char_whitelist","ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz ");
                _ocrEngine.PageSegMode = Emgu.CV.OCR.PageSegMode.SingleLine;
            }
            return _ocrEngine;
        }
        private string RunTesseract(Image<Gray, byte> img)
        {
            var engine = GetOcrEngine();
            engine.SetImage(img);
            engine.Recognize();
            return (engine.GetUTF8Text() ?? string.Empty).Trim();
        }

        //private void LoadOcrItems()
        //{
        //    _ocrItems = new List<OcrCheckItem>();
        //    _ocrIndex = 0;

        //    string path = setPath.FolderCheckOCR + global.Head + AppFilePath.OcrSettings;
        //    if (!File.Exists(path)) return;

        //    foreach (var raw in File.ReadAllLines(path))
        //    {
        //        string line = raw.Trim();
        //        if (line.Length == 0 || line.StartsWith("#")) continue;

        //        // X,Y,Width,Height,ExpectedText  (ExpectedText may itself contain commas)
        //        var parts = line.Split(new[] { ',' }, 5);
        //        if (parts.Length < 5) continue;

        //        try
        //        {
        //            _ocrItems.Add(new OcrCheckItem
        //            {
        //                X = Convert.ToInt32(parts[0].Trim()),
        //                Y = Convert.ToInt32(parts[1].Trim()),
        //                Width = Convert.ToInt32(parts[2].Trim()),
        //                Height = Convert.ToInt32(parts[3].Trim()),
        //                ExpectedText = parts[4].Trim()
        //            });
        //        }
        //        catch { /* skip malformed line */ }
        //    }
        //}


        //// ── Timeout stamp helpers ─────────────────────────────────────────────

        private void StampFailRead2d() {
            if (flag.Debug) { global.StopWatch.Reset(); return; }
            File.WriteAllText(setPath.ResultTxt, "Unreadable\r\nFAIL");
            this.Close();
        }

        private void StampFailCheckLed() {
            if (flag.Debug) { global.StopWatch.Reset(); return; }
            File.WriteAllText(setPath.ResultTxt, "(" + global.NumStep + ")time over\r\nFAIL");
            this.Close();
        }

        private void StampFailBlinkLed() {
            if (flag.Debug) { global.StopWatch.Reset(); return; }
            File.WriteAllText(setPath.ResultTxt, "(" + global.NumStep + ")time over\r\nFAIL");
            this.Close();
        }

        private void StampFailMultipleLed()
        {
            if (flag.Debug) { global.StopWatch.Reset(); return; }
            File.WriteAllText(setPath.ResultTxt, "(" + global.NumStep + ")time over\r\nFAIL");
            this.Close();
        }

        private void StampFailOcr()
        {
            if (flag.Debug) { global.StopWatch.Reset(); return; }
            File.WriteAllText(setPath.ResultTxt, "(" + global.NumStep + ")time over\r\nFAIL");
            this.Close();
        }

        private Stopwatch timeout = new Stopwatch();
        private bool flag_hsv_test = false;
    }
}
