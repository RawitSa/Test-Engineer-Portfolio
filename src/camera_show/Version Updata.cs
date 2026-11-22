using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace camera_show {
    class Version_Updata {
        //V1.02
        //Edit code to read easy
        //Edit file config from txt to csv
        //Edit function scan min max value camera to scan fast
        //Edit SmallChange set camera
        //Edit bug scan min max value camera
        //Add check digit sn
        //Add adjust degree
        //Add check led add step

        //V1.03
        //Add step fail in result.txt example "(3)OverTime\r\nFAIL"

        //V2022.04
        //Add path head 1 2 3 4

        //V2022.05
        //Add blinkLed mode

        //V2022.06
        //Add ReAdjust Camera

        //V2022.07
        //Edit set gamma to address camera

        //V2022.08
        //Edit set gamma and gain to address camera

        //V2022.09
        //Add saturation to address

        //V2022.10
        //Remove adjust camera in sat camera
        //Add save config to get config camera to csv

        //V2022.11
        //Add use port to open camera with port

        //V2023.01
        //Edit path log error CameraSetCapProp

        //V2023.02
        //Add log program catch all process

        //V2023.03
        //ลองเทสแล้วต้องใช้คู่กันถึงจะใช้ได้ทั้งตอนรันโค้ดและรัน exe อันล่างให้เอาไปใส่ใน Program.cs
        //AppDomain.CurrentDomain.UnhandledException 
        //Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);

        //V2023.04
        //Remove address gain

        //V2023.05
        //Add process timeDiv3 in read2d, checkLED, comparImage

        //V2023.06
        //ถ้ามันหา port ไม่เจอ ให้ตีเฟวได้เลย สำหรับไม่ใช่ดีบักโหมด

        //V2023.07
        //Edit image icon

        //V2023.08
        //เปลี่ยนวิธีการปรับกล้องใหม่ ตอนแรกเอา timeout มาหาร 3 แล้วแบ่งเป็น 2 ช่วง
        //ก็คือเปลี่ยนตอนช่วง 2 ให้นับต่อจากช่วงแรงไป 1 วินาที แล้วเข้าช่วง 2 ได้เลย

        //V2023.09
        //แก้เขียนคำผิด compare

        //V2023.10
        //add txt file config roi crop

        //2023.11
        //ตอนที่ set port ถ้าใน combobox เป็นค่าว่าง มันจะใส่ 0 เข้าไปแทน

        //2023.12
        //แก้บัค ตอนหา address ไม่เจอ มันจะวนเรื้อยๆ

        //2026.01
        //Major code reorganization using AI.

        //2026.10
        //Remove Compare mode (use CheckOCR instead)
        //Compare mode is back in the hub (CameraWorker.Compare.cs): light-proof template match, every part of the template must be present
        //Add parallel multi-camera mode (--hub / --multi): up to 4 cameras, Check LED / CheckOCR / Read2D / BlinkLed
        //Hub starts each head as soon as its test_head_N_steptest.txt is written, without waiting for other heads
        //Port is resolved per head at start time (Set Address = Gamma scans only free ports)
        //Right-click a camera tile in the hub window: live view, set port (only that camera changes; a previewing head on the same port is reset), frame size, camera values, address, process values
        //Set port in the tile menu also sets Set Address = Port: the head always opens that port; other heads never scan or take a port fixed by another head
        //Config / output folders are found automatically (AppPaths): works from host\mini_projeck or host\bin\Debug; override with camera_show_paths.txt
        //Started by the host (no arguments): first launch = hub, later launches forward their head to it; call_exe_tric.txt is written; hub closes after idle (camera_show_idle_exit.txt)
        //camera_show_mode.txt = single -> original Form1 per process
        //camera_show_port_source.txt = one mode name whose CSV decides the port of every mode (fixes ports that differ between cameraStep_*.csv)
        //Set port in the tile menu writes the port to every mode (not *_SetPort) and logs a WARNING when the CSVs disagree
        //Tiles: each head has its own colour; the selected / in-use camera gets a thick frame in that colour (click = select)
        //Tiles: mouse wheel = zoom that camera only (cut from the full-resolution frame), drag = move, middle click = 100%
        //Tiles: title shows the port really opened; double-click / Space = capture 1 frame; footer 'Remote (AnyDesk)' = 1 frame at a time (camera_show_remote.txt)
        //Hub window shows 8 cameras (MaxCameras = 8); ports 0..15
        //Tile menu: Draw ROI with the mouse, LED steps, HSV colour test, video format MJPG (test_head_N_fourcc.txt)
        //Tile menu: Start live view = runs the mode like a real job (OCR / 2D / LED / blink / compare), no time-out, no result file; 'picture only' = plain view for setup
    }
}
