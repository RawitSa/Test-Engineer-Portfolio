# Test Engineer Portfolio

**[ชื่อ-นามสกุล]** · Test Development Engineer (Electronics) → Test Engineer (Software / Application)
ติดต่อ: [อีเมล] · [LinkedIn] · [เบอร์โทร]

## แนะนำตัว

ทำงานด้าน Test Development Engineer ฝั่ง electronics: ออกแบบและพัฒนาเครื่องมือทดสอบ
ชุดทดสอบ (test fixture) และซอฟต์แวร์ควบคุมการทดสอบ ตั้งแต่วางแผนการทดสอบ ลงมือทดสอบบนเครื่องจริง
ไปจนถึงรายงานและติดตามข้อบกพร่อง กำลังย้ายสายมาเป็น Test Engineer ฝั่ง software / application
โดยนำทักษะเดิมมาต่อยอด และ portfolio นี้รวมผลงานจริงพร้อมตัวอย่างเอกสารทดสอบที่เขียนจากงานเหล่านั้น

## หมายเหตุเรื่องความโปร่งใส

- ตัดชื่อบริษัท ลูกค้า รหัสผลิตภัณฑ์ และข้อมูลภายในออกทั้งหมด
- ตัวเลขทุกตัวระบุที่มา (ภาพสังเคราะห์ / ภาพจริง / โค้ดจำลอง) และแยกจากผลบนหน้างานจริง
- ซอร์สโค้ดใน [src/camera_show](src/camera_show/README.md) เป็นเวอร์ชันที่ build และรันผ่านบนเครื่องจริง ไม่รวม DLL ของบุคคลที่สาม
  และไลบรารีภายใน (`SetupPay`) ดูรายการใน [lib/README.md](src/camera_show/lib/README.md) ประวัติเวอร์ชันในโค้ด (`Version Updata.cs`) ระบุว่าปี 2026.01 มีการจัดโครงสร้างโค้ดใหม่โดยใช้ AI ช่วย
- งานเหล่านี้พัฒนาร่วมกับ AI assistant (Claude) โดยผมเป็นผู้กำหนดความต้องการ ทดสอบบนเครื่องจริง
  พบและรายงานข้อบกพร่อง และตัดสินใจเลือกแนวทาง `[ตรวจสอบและแก้ข้อความนี้ให้ตรงกับสิ่งที่คุณทำจริง]`

## ผลงาน

| # | โปรเจกต์ | สิ่งที่แสดงให้เห็น |
|---|----------|--------------------|
| 1 | [ระบบตรวจ LCD symbol ด้วยกล้อง + AI](projects/01-vision-inspection.md) | risk-based testing (false pass), threshold design, regression, multi-LED check |
| 2 | [ระบบทดสอบ PCBA 12 station พร้อมกัน](projects/02-multi-station-pcba-test-system.md) | test architecture, hardware integration (NI cDAQ, RS485), live monitoring UI |
| 3 | [การอ่าน DataMatrix ที่มีคราบ flux](projects/03-datamatrix-read-reliability.md) | วิเคราะห์ปัญหา, เปรียบเทียบเครื่องมือด้วยข้อมูล, รู้ขีดจำกัด |

## ซอร์สโค้ด

- [src/camera_show](src/camera_show/README.md) — ระบบตรวจด้วยกล้องหลายหัว (C#, WinForms, Emgu CV) พร้อมโมดูล AI ตรวจ symbol, ตัวตามชิ้นงาน, โหมด Multi-LED
- [docs/Camera_Show_Modes_Report.pptx](docs/Camera_Show_Modes_Report.pptx) — สไลด์อธิบายโหมดตรวจและหลักการทำงาน

## เอกสารทดสอบตัวอย่าง

- [Bug reports](bug-reports/) — 4 ฉบับ จากบั๊กที่พบจริงระหว่างทดสอบ
- [Test cases](test-cases/test-cases-multi-led-and-roi.md) — โหมดเช็ค LED หลายดวง และการวาด ROI
- [Test plan](test-plan/test-plan-vision-inspection.md) — แผนทดสอบระบบตรวจด้วยกล้อง

## ทักษะที่เชื่อมกับงาน Test Engineer

| ทักษะ | หลักฐานใน repo |
|-------|-----------------|
| ออกแบบ test case จาก spec | [test-cases](test-cases/test-cases-multi-led-and-roi.md) |
| เขียน bug report ที่ทำซ้ำได้ | [bug-reports](bug-reports/) |
| มองความเสี่ยง (ของเสียหลุดผ่าน > ของดีโดนตีตก) | [โปรเจกต์ 1](projects/01-vision-inspection.md) |
| Regression testing | [BUG-003](bug-reports/BUG-003-tracking-lost-when-rotating.md) |
| Test automation / อ่านผลจากเครื่องมือวัด | [โปรเจกต์ 2](projects/02-multi-station-pcba-test-system.md) |
| วิเคราะห์ root cause และแยกข้อเท็จจริงออกจากข้อสันนิษฐาน | bug reports ทุกฉบับ |
| เก็บข้อมูลจริงมาปรับปรุงโมเดล / เกณฑ์ | [โปรเจกต์ 1](projects/01-vision-inspection.md), [โปรเจกต์ 3](projects/03-datamatrix-read-reliability.md) |

เครื่องมือ: C# (WinForms, Emgu CV), Python, NI-DAQmx (cDAQ), RS485, Git
กำลังเสริม: API testing (Postman), SQL, Agile / Jira, ISTQB Foundation `[ลบหรือแก้ตามจริง]`
