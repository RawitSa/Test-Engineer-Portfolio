# 02 — ระบบทดสอบ PCBA 12 station พร้อมกัน (prototype)

## บริบท
ต้องทดสอบบอร์ด PCBA 12 หัว (station) พร้อมกัน โดยมี PLC และ DAQ ร่วมกัน และต้องเห็นค่าของแต่ละหัวแบบ real-time บนเครื่องทดสอบ

## Hardware
- NI cDAQ-9173 (ต่อ USB) + NI 9205 (วัดแรงดัน 32 ช่อง) + NI 9227 (วัดกระแส) 3 ชุด
- Relay card 12 ใบ ควบคุมผ่าน RS485 สายเดียว แยกด้วย address ใช้ DLL (managed .NET) ที่ทำเอง

## สถาปัตยกรรม

```
 TestSystem.UI (WinForms, .NET 8)
   MainForm ── Timer 1 วินาที ──► อ่าน LiveStatusStore ──► StationPanel x 12
        │
        ▼
 TestSystem.Core
   TestOrchestrator ── Task.WhenAll ──► TestStation x 12 (แยกอิสระ error ไม่ลามกัน)
        │                                   │
        │ Channel<TestResult>               ├─► Rs485RelayBus (COM เดียว, SemaphoreSlim คิวคำสั่ง)
        ▼                                   ├─► PLC client (shared)
   HttpReportSender (ส่งผลขึ้นเว็บ)         └─► IDaqReader ──► DaqHostService (.NET Framework 4.8)
                                                                   └─► NI-DAQmx ──► cDAQ-9173
```

## การตัดสินใจออกแบบ
| เรื่อง | ตัดสินใจ | เหตุผล |
|--------|-----------|--------|
| Relay RS485 | บัสเดียว + คิวคำสั่ง (`SemaphoreSlim`) | RS485 เป็น half-duplex ส่งได้ทีละคำสั่ง จึงไม่ทำ 12 connection แยก |
| การ์ด relay แต่ละใบ | `Rs485RelayCardAdapter` implement `IRelayController` | `TestStation` ไม่ต้องรู้ว่าเป็น TCP หรือ RS485 |
| NI-DAQmx | แยกเป็นโปรเซส .NET Framework 4.8 คุยผ่าน IPC | ข้อมูลที่ใช้ตอนออกแบบระบุว่า DAQmx .NET API ยังไม่รองรับ .NET 5+ (ควรตรวจสอบกับ NI ก่อนยึดตามนี้) |
| อัปเดตหน้าจอ | `LiveStatusStore` (`ConcurrentDictionary`) + `Timer` 1 วินาที | ทั้ง 12 station เขียนพร้อมกันได้ปลอดภัย และ Timer รันบน UI thread ไม่ต้อง Invoke ข้าม thread |
| แต่ละหัวมีหลาย step | `TestStepView` ลำดับชั้น (1, 1.1, 1.2, 2 ...) | ตารางไล่กรอกทีละแถวเหมือนหน้าจอเดิม |

## หน้าจอ
UI คล้ายแบบเดิม: 1 panel ต่อ 1 head (3 คอลัมน์ x 4 แถว) แสดง serial number, ตัวนับ, banner สี
(IDLE / TESTING / PASS / FAIL / ERROR), progress bar และตาราง step (Spec / Measure / Result) แถวหมวดสีส้ม

## ข้อบกพร่องที่พบระหว่างพัฒนา
- [BUG-004](../bug-reports/BUG-004-datagrid-null-reference.md) โปรแกรมล่มเพราะค้นหาคอลัมน์ด้วยชื่อที่ไม่ได้ตั้งไว้

## สถานะและข้อจำกัด
- เป็น prototype: การเชื่อม PLC / DLL relay จริงยังมี TODO และ step template เป็นตัวอย่างตามโครงสร้างหน้าจอ ไม่ใช่ spec จริงของบอร์ด
- ปุ่ม TEST/EXIT ต่อ station ปิดไว้ เพราะใช้ `CancellationToken` ตัวเดียวคุมทั้ง 12 station ต้องแก้ `TestOrchestrator` ให้แยก token ต่อ station
- ยังไม่ได้ทดสอบกับ hardware ครบชุด

## บทเรียน
- เริ่มจากข้อจำกัดของ hardware (RS485 half-duplex, ข้อจำกัดของ driver) แล้วค่อยออกแบบซอฟต์แวร์
- ใช้ interface คั่น hardware ทำให้สลับเป็นตัวจำลองเพื่อทดสอบ logic โดยไม่ต้องมีเครื่องจริง
