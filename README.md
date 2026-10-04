# JNN PDF — So sánh & Kiểm tra Bản vẽ

Phần mềm (Desktop & Web) để so sánh bản vẽ Ver1/Ver2 và kiểm tra (QA) nội dung bản vẽ kỹ thuật / PDF.

## Chạy
```
pip install -r requirements.txt
run.bat                      # hoặc: python -m pdf_qa ver1.pdf ver2.pdf
```
Có thể kéo thả 1 hoặc 2 file PDF vào cửa sổ.

## Phiên bản Web
```
pip install -r requirements.txt
run_web.bat                  # hoặc: python -m pdf_qa_web  → mở http://localhost:8765
python -m pdf_qa_web --host 0.0.0.0   # cho đồng nghiệp trong mạng LAN truy cập http://<IP-máy>:8765
```
- Backend FastAPI dùng lại nguyên `pdf_qa/core.py` (PyMuPDF) → kết quả giống hệt bản desktop.
- Frontend HTML/JS thuần (`pdf_qa_web/static`), không cần build. Đủ các chức năng bên dưới.
- File upload lưu tạm ở `%TEMP%\pdf_qa_web`, tự xoá sau 24h. Template / markup JSON tương thích 2 chiều với bản desktop.
- Phím tắt mở file trên web: `Ctrl+O` (PDF 1), `Ctrl+Shift+O` (PDF 2) — trình duyệt chiếm `Ctrl+1/2`.

## Chức năng
| Nhóm | Mô tả |
|---|---|
| **So sánh Ver1 ↔ Ver2** | Xem Ver1 / Ver2 / Song song (đồng bộ zoom-pan) / Overlay (Đỏ = chỉ Ver1, Xanh = chỉ Ver2, Xám = không đổi). Diff nét vẽ (pixel) + diff text (xoá / thêm / sửa `1200 → 1500`). Danh sách khác biệt, bấm vào để zoom tới. So sánh tất cả trang, xuất Excel. |
| **✂ Crop** | Kéo khung → xuất PDF vector (giữ nét & text) hoặc PNG, cho Ver1/Ver2 và ảnh overlay khác biệt. |
| **🔎 Đọc data** | Kéo khung → đọc text trong vùng của cả 2 bản, so sánh từng dòng, tô màu khác biệt, preview overlay vùng. Copy / xuất CSV-Excel. |
| **▣ Vùng check** | Định nghĩa mini vùng + quy tắc: So sánh Ver1↔Ver2, Chỉ trích xuất data, Chứa text, Bằng chính xác, Regex, Không rỗng, Số trong khoảng. Kết quả OK / CHANGED / FAIL / DATA tô màu trên bản vẽ. Tick **Áp dụng cho tất cả trang** để vẽ 1 lần, kiểm tra mọi trang. **📁 Kiểm tra nhiều file PDF** áp vùng cho cả bộ bản vẽ → bảng kết quả + Excel (sheet chi tiết & sheet data). Lưu/mở template JSON. |
| **⌕ Query** | Tìm text / số / regex theo phạm vi (trang, tất cả, trong vùng check, trong vùng đọc data), highlight màu tuỳ chọn; lọc chỉ các kết quả có thay đổi giữa 2 bản. |
| **🖍 Highlight** (G) | Giống Bluebeam: kéo qua chữ → tô theo dòng chữ, kéo vùng trống → tô vùng; màu & độ đậm tuỳ chọn, chế độ multiply (nét đen vẫn rõ). Chuột phải: ghi chú / đổi màu / độ đậm / xoá. Ctrl+Z hoàn tác. Danh sách markup, **Lưu vào PDF** thành annotation thật (mở bằng Bluebeam/Acrobat), xuất Excel, lưu/mở markup JSON. |

## Phím tắt
`Ctrl+1/2` mở Ver1/Ver2 · `1-4` chế độ xem · `H/Esc` Pan · `C` Crop · `R` Đọc data · `Z` Vùng check ·
`Ctrl+D` so sánh trang · `PgUp/PgDn` đổi trang · `F` Fit · lăn chuột = zoom · chuột giữa = pan.

## Ghi chú
- Nếu Ver2 bị dịch chuyển so với Ver1, chỉnh **Lệch Ver2 dx/dy** (pt) trong tab So sánh.
- Quy tắc text của vùng check áp dụng cho Ver2 (nếu có), ngược lại Ver1.
- Text chỉ đọc được với PDF có lớp text (xuất từ CAD). PDF scan cần OCR (chưa hỗ trợ).
