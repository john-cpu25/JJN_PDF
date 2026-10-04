# JNN PDF – PDF Review & Construction Drawing Tool (prototype v0.1)

Ứng dụng desktop C# / WPF / .NET 8 để review bản vẽ xây dựng: markup, đo đạc, takeoff, so sánh / overlay revision (PDF 1 vs PDF 2), đọc data theo vùng, vùng check, crop, QA/QC, drawing register và report.
Lấy cảm hứng từ workflow của Bluebeam nhưng không dùng giao diện, logo hay code của Bluebeam. Chạy offline hoàn toàn.

---

## 1. Kiến trúc

```
View (XAML)  ──binding/commands──►  ViewModel  ──►  Services  ──►  Models
MainWindow.xaml / App.xaml          MainViewModel     PdfService (PDFium)
MainWindow.xaml.cs (IViewer)                          CompareService / OverlayService
PdfCanvas (view + markup engine)                      MeasurementService / TakeoffService
Dialogs / BatchWindow                                 ZoneService / OcrService
                                                      ExportService / ProjectService / DatabaseService
```

| File | Nội dung |
|---|---|
| `App.xaml`, `App.xaml.cs` | **UI**: theme Dark/Light, style, converter, bắt lỗi toàn cục, `--selftest` |
| `MainWindow.xaml` | **UI**: menu, toolbar, thumbnails / bookmarks / search, canvas, panel phải, Markups List, status bar |
| `MainWindow.xaml.cs` | Code-behind mỏng: nối canvas ↔ ViewModel (`IViewer`), phím tắt, kéo thả, crash recovery |
| `ViewModels.cs` | **ViewModel**: `MainViewModel` (project, commands, undo/redo, autosave, compare, zone, QA/QC, export) |
| `PdfService.cs` | **PDF engine**: mở PDF (password / corrupt), render async theo tile, cache LRU, text, search, bookmarks |
| `PdfCanvas.cs` | **View + markup engine**: zoom / pan / rotate, overlay, các tool markup và đo, chọn / di chuyển / resize |
| `Services.cs` | **Logic**: Logger, Geometry, Measurement, Takeoff, **DatabaseService (SQLite)**, Project (JSON + autosave + backup), Undo, Compare, Overlay, Zone, OCR |
| `ExportService.cs` | Xuất CSV / Excel, PDF có markup (flatten), crop PDF, merge, stamp, sửa trang, report PDF, in |
| `Models.cs` | Model: Markup, Issue, Zone, DrawingSheet, ScaleSetting, ProjectData, … |
| `Dialogs.cs` | Hộp thoại (Prompt, Scale, Calibrate, Report) và `BatchWindow` |

**Hiệu năng:** không render cả file một lúc.
- Trang được render ở 2 lớp: ảnh nền ~1800 px, cộng tile độ phân giải cao chỉ cho vùng đang nhìn (async, có huỷ, có debounce).
- Cache bitmap LRU; thumbnail chỉ render khi cuộn tới (lazy, virtualized).
- Các tác vụ nặng (search, compare, OCR, zone check, register) chạy nền với `CancellationToken` và thanh tiến trình.

## 2. Code
Toàn bộ source nằm trong thư mục này, khoảng 7.500 dòng (xem bảng trên).

## 3. Tạo / mở project trong Visual Studio
1. Cài Visual Studio 2022 (17.8+) với workload **.NET desktop development**.
2. Mở `JnnPdf.csproj` (File → Open → Project/Solution).
3. Chọn cấu hình `Debug | x64`.

Nếu muốn tạo project mới từ đầu:
- Chạy `dotnet new wpf -n JnnPdf`.
- Chép các file `.cs` và `.xaml` vào.
- Thay `.csproj` bằng file trong thư mục này.

## 4. NuGet packages
```powershell
dotnet add package PDFiumCore --version 156.0.8076     # render + text (PDFium, có sẵn DLL native)
dotnet add package PDFsharp --version 6.2.4            # ghi PDF: markup, crop, merge, report
dotnet add package ClosedXML --version 0.105.1         # Excel
dotnet add package Microsoft.Data.Sqlite --version 8.0.11  # SQLite local
```
OCR dùng `Windows.Media.Ocr` có sẵn trong Windows 10/11 (TFM `net8.0-windows10.0.19041.0`), không cần package.

## 5. Build
```powershell
cd JnnPdf
dotnet build -c Release
```
Đóng gói một thư mục chạy độc lập (không cần cài .NET):
```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

## 6. Chạy
```powershell
dotnet run                                   # hoặc
bin\Release\net8.0-windows10.0.19041.0\win-x64\JnnPdf.exe  "D:\Drawings\A.pdf"
```
Kiểm tra engine không cần giao diện:
```powershell
JnnPdf.exe --selftest A.pdf B.pdf D:\out
```
Kết quả nằm trong `D:\out\selftest.log`.

Dữ liệu local (SQLite, log, autosave, backup) nằm ở `%LocalAppData%\JNNPDF`.

## 7. Test từng chức năng
| Chức năng | Cách test |
|---|---|
| Mở PDF | Bấm OPEN PDF / Ctrl+O, hoặc kéo thả file vào cửa sổ. File có mật khẩu sẽ hỏi password; file hỏng sẽ báo lỗi rõ ràng. |
| Viewer | Lăn chuột để zoom; chuột giữa hoặc giữ Space để pan; Ctrl+0 fit page; xoay bằng nút ⟳; PgUp/PgDn; ô số trang + Enter; Ctrl+G đến trang. |
| Thumbnails | Click để chuyển trang. Kéo thả để đổi thứ tự. Chuột phải để xoay / nhân bản / xoá trang. Lưu lại bằng File → Save PDF As. |
| Markup | Chọn tool Text, Callout, Line, Arrow, Rect (R), Cloud (C), Polygon, Pen, Highlight, RFI/TBC…, Stamp, Link rồi vẽ. Dùng V để chọn, kéo để di chuyển, kéo handle để resize, double-click để sửa text, Delete để xoá. |
| Đo | Bấm **Scale** (vd 1:100) hoặc dùng **Calibrate** (vẽ một đoạn đã biết độ dài). Length (M) cho kết quả dạng `Length = 4.25 m`. Area: click các đỉnh rồi Enter/double-click. Count: click nhiều lần rồi Enter. Angle: 3 click. |
| Markups List | Có sort, search, lọc Status/Type, Group, sửa Subject/Comment/Status trực tiếp. Double-click một dòng để zoom tới markup. Xuất Excel/CSV. |
| Undo / Redo | Ctrl+Z / Ctrl+Y sau khi tạo, xoá, di chuyển, sửa markup. |
| Save / Load | Ctrl+S lưu file `.jnnreview` (JSON, đường dẫn PDF tương đối). Mở lại bằng OPEN PROJECT. Autosave chạy mỗi phút, giữ `_backup_001..003`. Nếu app bị kill, lần mở sau sẽ hỏi khôi phục. |
| Compare | Mở tab Compare → **Mở PDF 2** → **So sánh tất cả**. Tab Changes liệt kê các vùng thay đổi; dùng F3 / Shift+F3 để đi tới thay đổi tiếp / trước, rồi Export PDF/Excel. |
| Overlay | Chế độ: PDF 1 only, PDF 2 only, PDF 1+PDF 2 tô màu (đỏ/xanh), Transparency (slider), Blink. Căn chỉnh bằng Move X/Y, Rotate, Scale hoặc Alt+mũi tên. |
| Đọc data vùng | Tool **Đọc data** (D): kéo một vùng để xem text của PDF 1 và PDF 2 cùng phần khác biệt. Nút Copy để copy kết quả. |
| Vùng check | Tool **Vùng check**: kéo vùng → đặt tên → chọn rule (`extract`, `not_empty`, `equals`, `contains`, `regex`, `number_range`, `compare` = so với PDF 2) → **Chạy**. Kết quả PASS/FAIL cho tất cả các trang hiện trong tab Zone Results; xuất Excel. |
| Crop | Tool **Crop**: kéo vùng → xuất PDF vector, PNG 300 dpi hoặc copy text. |
| Takeoff | Mở tab Takeoff, bật Takeoff mode, chọn Category, Name, Depth. Các phép đo sau đó sẽ được cộng dồn theo Category/Unit (m, m², m³, ea). Xuất Excel. |
| QA/QC | Dùng tool Issue pin hoặc nút + Issue. Sửa Severity, Assigned, Status, Due trực tiếp trên bảng. "Bước tiếp" chuyển trạng thái NOT CHECKED → … → APPROVED. |
| Register | Register tự đọc Sheet / Title / Rev từ title block (hoặc từ zone tên Sheet/Title/Rev). Có filter, sort, xuất Excel/CSV. |
| Search / OCR | Ctrl+F, ví dụ tìm `200x400`; click kết quả để zoom tới. Với bản scan: Review → OCR, sau đó có thể Search, Đọc data và xuất PDF searchable. |
| Hyperlink | Tool Link: kéo vùng → nhập đích (`45`, `sheet:S-101`, `https://…` hoặc file PDF). Click link bằng tool Select để đi tới đích. |
| Batch | Review → Batch: merge, stamp, export PNG, zone check cả thư mục, compare thư mục A với B, rename theo sheet. |
| Report | Bấm Report → điền thông tin → xuất PDF, Excel hoặc CSV (Summary, Issues, Measurements, Takeoff, Markups, Register). |

## 8. Roadmap
1. Xuất markup thành **PDF annotation thật** (có thể chỉnh sửa tiếp trong Acrobat/Bluebeam) và import annotation có sẵn trong PDF.
2. Mở nhiều tài liệu (tab), xem split view PDF 1 | PDF 2 cuộn đồng bộ.
3. Tool Chest: thư viện markup/symbol tuỳ biến, legend, layer, profile công cụ theo bộ môn.
4. Compare nâng cao: so sánh theo vector (đối tượng đường/text), tự căn chỉnh bằng điểm mốc.
5. Đo nâng cao: thể tích, cắt trừ lỗ mở trong Area, snap vào nét vector, scale theo viewport.
6. Cộng tác: server dùng chung (SQL Server/Postgres) hoặc đồng bộ qua SharePoint/ACC, quản lý user và phân quyền.
7. OCR đa ngôn ngữ (Tesseract), nhận dạng title block bằng AI, tự tạo register.
8. Tích hợp BIM: liên kết sheet với model Revit/IFC, issue BCF, clash từ overlay Structural vs Architectural.
9. Hiệu năng: render GPU, cache thumbnail ra đĩa, xử lý PDF > 1 GB.
10. Đóng gói MSIX, auto-update, license, telemetry tuỳ chọn.
