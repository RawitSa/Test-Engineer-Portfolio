# camera_show — ระบบตรวจสอบด้วยกล้องหลายหัว (Vision Inspection Hub)

แอป Windows ใช้กล้องตรวจผลิตภัณฑ์บนสายการทดสอบ: อ่านรหัส 2D, ตรวจ LED, ตรวจ symbol บนจอ LCD (template + AI)
และส่งผล PASS/FAIL ให้โปรแกรมหลัก (host) ผ่านไฟล์ผลลัพธ์ รองรับหลายกล้อง (หลาย "head") ทำงานพร้อมกัน

- **Stack:** C#, WinForms, .NET Framework 4.8, Emgu CV (OpenCV), ZXing.Net, ZedGraph
- **ขนาด:** ราว 9,800 บรรทัด (ไม่รวมไฟล์ Designer)
- **สถานะ:** เวอร์ชันนี้ build และรันผ่านบนเครื่องจริง (ผู้พัฒนายืนยัน) รวม AI ตรวจ symbol, การชดเชยซูม, โหมด multi_led
  **ยังไม่รวม** การอัปเดต read2d ที่ออกแบบไว้ (ดู [โปรเจกต์ 3](../../projects/03-datamatrix-read-reliability.md))
- ประวัติเวอร์ชันอยู่ใน `Version Updata.cs` (2022 → 2026) รวมบันทึกว่าปี 2026.01 มีการจัดโครงสร้างโค้ดใหม่โดยใช้ AI ช่วย

## โหมดตรวจ

| โหมด | หน้าที่ |
|------|---------|
| Normal | ดูภาพสดอย่างเดียว |
| Read2D | ตัด ROI → ค้นหากรอบด้วย Canny → ถอดรหัส ZXing ขณะหมุนภาพ ถ้าพลาดเลื่อน ROI ตามเกลียว |
| CheckOCR | อ่านข้อความเทียบกับข้อความที่คาดหวัง |
| Compare Image | ตรวจ symbol บนจอ LCD ทีละ symbol ด้วย template + AI (ดูหัวข้อถัดไป) |
| Check LED | ติดต่อเนื่องครบเวลา วัดจากพิกเซลในช่วงสี HSV |
| Multi-LED | เช็ค LED หลายดวงพร้อมกัน เลือกได้ เช่น `on=1-3,5-7,9-10` หรือ `all` ([LedSelect.cs](Services/LedSelect.cs)) |
| Blink LED | วัดความถี่กะพริบ ผลขึ้นกับ frame rate ของกล้อง จึงไม่มี Sleep ในลูป |

## ตรวจ symbol ด้วย AI (ส่วนที่น่าสนใจที่สุด)

| ไฟล์ | หน้าที่ |
|------|---------|
| `SymbolNet.cs` | รัน CNN ขนาดเล็ก (เทรนจากโน้ตบุ๊ก) ด้วย C# ล้วน ไม่ใช้ ONNX/OpenCV dnn จึงไม่ผูกกับเวอร์ชัน Emgu |
| `SymbolAI.cs` | ให้คะแนนว่า "symbol นี้อยู่ครบและเป็นตัวนี้จริงไหม" ใช้ภาพ 64x64 ของกรอบ symbol + ขอบ 12% |
| `SymbolTracker.cs` | หา LCD อีกครั้งหลังชิ้นงานขยับ โดยให้ symbol ที่ตรงกันโหวตตำแหน่งเดียว ตัด outlier ออก |
| `SymbolAligner.cs` | เก็บการเลื่อน/หมุน (0–360°) และซูมเป็นเมทริกซ์เดียว แปลงภาพสดกลับเป็นภาพตอนสอน |
| `SymbolMatcher.cs`, `TemplateMatcher.cs` | วิธีเดิม: correlation และ edge match ของ template |
| `SymbolCalib.cs` | เรียนรู้เกณฑ์รายตัวจากของดีจริง (`sym_learn=1`) แทนเลขเดียวสำหรับทุก symbol |
| `SymbolDebug.cs` | เขียนภาพ debug แบบ real-time ลงโฟลเดอร์ `debug/` เพื่อดูว่าระบบ "เห็น" อะไร |

ค่าที่ปรับได้ใน `camera_show_template.txt`: `ai` (0–3), `ai_min` (เกณฑ์ความมั่นใจ), `ai_shift`, `ai_collect`
รายละเอียดอยู่ใน [docs/README_AI.txt](docs/README_AI.txt) และ [docs/README_MULTI_LED.txt](docs/README_MULTI_LED.txt)
ถ้าไม่มีไฟล์โมเดล ระบบกลับไปใช้วิธีเดิมอัตโนมัติ และ `ai_collect=1` จะเก็บภาพที่ AI ไม่มั่นใจหรือขัดกับระบบเดิมไว้สำหรับเทรนซ้ำ

## โครงสร้างโค้ด

| โฟลเดอร์ / ไฟล์ | หน้าที่ |
|------------------|---------|
| `Services/CameraHub.cs` | ตัวกลางเดียวที่ดูแลงานทุกกล้อง: 1 head = 1 thread, มีคิวเมื่อเกินจำนวนกล้องสูงสุด (8) |
| `Services/CameraWorker.*.cs` | โค้ดของแต่ละโหมด (Compare, MultiLed, Read2d, Blink, Symbols) |
| `Services/PortResolver.cs` | หาว่า head ไหนใช้กล้อง (port) ตัวไหน ตอนงานเริ่ม ไม่รบกวนกล้องที่กำลังทำงาน |
| `Services/AppPaths.cs` | หาโฟลเดอร์ config/output อัตโนมัติ ใช้ได้เมื่อ host เรียกจากโฟลเดอร์อื่น |
| `Services/JobSpec.cs`, `StepConfig.cs` | คำสั่งตรวจ 1 งาน และค่าตั้งของแต่ละ step (จาก CSV) |
| `Services/FormMultiView.cs`, `TileView.cs`, `LivePreview.cs` | หน้าต่าง hub แบบ tile หลายกล้อง, ภาพสด, วาด ROI |
| `Camera/` | ตั้งค่ากล้อง, ปรับ min/max อัตโนมัติ, ปรับกล้องใหม่ |
| `Form1.*.cs` | หน้าต่างเดิมแบบ 1 กล้อง/1 โปรเซส (สลับกลับได้ด้วย `camera_show_mode.txt`) |
| `Constants/`, `Models/` | ค่าคงที่ และข้อมูลที่ใช้ร่วมกัน |
| `docs/INTEGRATION_NOTES.txt` | วิธีที่ host เรียก exe, ไฟล์ที่ใช้สื่อสารกัน, การจัดการ port กล้อง |

## Build

1. เปิด `camera_show.sln` ด้วย Visual Studio (Target: .NET Framework 4.8, AnyCPU)
2. วาง DLL ที่จำเป็นในโฟลเดอร์ `lib/` ตาม [lib/README.md](lib/README.md) (ไม่ได้รวมมาใน repo)
3. Build แล้ววางโฟลเดอร์ `config/` ข้าง exe โดยเริ่มจาก [config.example/](config.example/)
4. รันแบบ hub: `camera_show.exe --hub`

## ข้อจำกัดและเรื่องที่ทราบ

- **ยังไม่มี automated test** ตัวที่เป็น logic ล้วน (เช่น parser รายการ LED ใน `LedSelect.cs`) เหมาะจะเริ่มเขียน unit test ก่อน
  ชุด test case ที่ออกแบบไว้อยู่ที่ [test-cases](../../test-cases/test-cases-multi-led-and-roi.md)
- **ค่าซูมที่จำไว้เป็น static ร่วมกันทุก head** (`SymbolAligner.LastScale`) เมื่อรันหลายกล้องพร้อมกันอาจรบกวนกัน ควรแยกต่อ head
- **ควรตรวจ:** กรณี symbol อยู่นอกภาพ ว่า AI ไม่ตัดสินให้ผ่าน
- **รหัสผ่านของหน้าตั้งค่ากล้องฝังอยู่ในโค้ด** (2 จุด) เป็นเพียงตัวกันการกดผิดของ operator ไม่ใช่ระบบความปลอดภัย
  ควรย้ายไปเก็บเป็นค่า config หรือ hash ก่อนใช้นอกสายการผลิต
- ตัวเลขความแม่นยำของ AI วัดจากภาพสังเคราะห์และภาพจริงชุดเล็ก ยังไม่ใช่อัตราของเสียหลุดผ่านบนสายจริง
  (ดู [โปรเจกต์ 1](../../projects/01-vision-inspection.md))
