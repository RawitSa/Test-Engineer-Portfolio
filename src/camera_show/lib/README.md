# lib/ — DLL ที่ต้องวางเองก่อน build

repo นี้ไม่รวมไฟล์ไบนารี (`.dll`) ให้ดาวน์โหลดหรือคัดลอกมาวางในโฟลเดอร์นี้ ให้ชื่อตรงกับที่ `camera_show.csproj` อ้างอิง

| ไฟล์ | คืออะไร | ที่มา |
|------|---------|-------|
| `Emgu.CV.World.dll` (+ `cvextern.dll` และไฟล์ native อื่นของ Emgu ไว้ข้าง exe) | Emgu CV: ตัวห่อ OpenCV สำหรับ .NET | NuGet / emgu.com ใช้เวอร์ชันเดียวกับที่ build ผ่าน |
| `zxing.dll`, `zxing.presentation.dll` | ZXing.Net 0.16.4 (อ่านบาร์โค้ด/QR/DataMatrix) | NuGet: `ZXing.Net` 0.16.4 |
| `ZedGraph.dll` | กราฟ (5.1.7) | NuGet: `ZedGraph` |
| `Spire.XLS.dll` | อ่าน/เขียน Excel ของ E-iceblue (เชิงพาณิชย์ มีรุ่นฟรีที่จำกัดขนาด) | ตรวจว่ายังใช้จริงหรือไม่ (`using Spire.Xls...` ใน `Form1.Setup.cs`, `Form1.Modes.cs`) ถ้าไม่ใช้ ลบ reference และ using ได้ |
| `SetupPay.dll` | ไลบรารีภายในสำหรับอ่าน/เขียนค่าใน CSV config (`read_text`, `write_text`) | **ไม่รวมใน repo** ถ้าจะ build ที่อื่น ต้องเขียนสองเมธอดนี้ขึ้นใหม่ |

นอกจากนี้ โหมด OCR ต้องมีไฟล์ภาษา (tessdata) ในโฟลเดอร์ `config/tessdata/` ไม่ได้รวมมาเพราะมีขนาดใหญ่ (ดาวน์โหลดได้จาก repository ของ Tesseract)
