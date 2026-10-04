// ============================================================================
//  ExportService.cs  –  EXPORT / PDF WRITING (PDFsharp + ClosedXML)
//  * CSV / Excel tables (markups, issues, takeoff, register, zone results)
//  * Flatten markups into a PDF (vector, rotation-safe raw content stream)
//  * Comparison PDF, Crop to PDF, Merge, Batch stamp, Page operations
//  * PDF report (summary / issues / measurements / takeoff / markups)
//  * Searchable PDF from OCR, PNG export, Print
// ============================================================================
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClosedXML.Excel;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Advanced;
using SharpDoc = PdfSharp.Pdf.PdfDocument;

namespace JnnPdf;

/// <summary>Generic table used by CSV/Excel/PDF report exporters.</summary>
public sealed record TableData(string Name, string[] Headers, List<object?[]> Rows);

public static class ExportService
{
    static ExportService()
    {
        // PDFsharp 6 (core build) needs to be told it may use the Windows fonts folder.
        try { GlobalFontSettings.UseWindowsFontsUnderWindows = true; } catch { /* already set */ }
    }

    // ===================================================================== tables
    public static TableData MarkupsTable(IEnumerable<Markup> list) => new("Markups",
        new[] { "ID", "Page", "Type", "Subject", "Text", "Comment", "Author", "Date", "Status", "X", "Y", "Width", "Height", "Measurement", "Color" },
        list.Select(m => new object?[] { m.Id, m.PageNumber, m.Type.ToString(), m.Subject, m.Text, m.Comment, m.Author,
            m.CreatedDate.ToString("yyyy-MM-dd HH:mm"), m.Status, m.X, m.Y, m.Width, m.Height, m.ValueText, m.Color }).ToList());

    public static TableData IssuesTable(IEnumerable<Issue> list) => new("Issues",
        new[] { "Issue ID", "Sheet", "Page", "Location", "Category", "Description", "Severity", "Assigned To", "Status", "Due Date", "Reviewer" },
        list.Select(i => new object?[] { i.IssueId, i.Sheet, i.PageNumber, i.Location, i.Category, i.Description, i.Severity,
            i.AssignedTo, i.Status, i.DueDate?.ToString("yyyy-MM-dd"), i.Reviewer }).ToList());

    public static TableData TakeoffTable(IEnumerable<TakeoffSummary> list) => new("Takeoff",
        new[] { "Category", "Name", "Type", "Quantity", "Unit", "Items", "Pages" },
        list.Select(t => new object?[] { t.Category, t.Name, t.Type, t.Quantity, t.Unit, t.Items, t.Pages }).ToList());

    public static TableData MeasurementsTable(IEnumerable<Markup> list) => new("Measurements",
        new[] { "ID", "Page", "Type", "Value", "Result", "Category", "Name", "Subject" },
        list.Where(m => m.IsMeasurement).Select(m => new object?[] { m.Id, m.PageNumber, m.Type.ToString(), Math.Round(m.Value, 3), m.ValueText, m.Category, m.TakeoffName, m.Subject }).ToList());

    public static TableData RegisterTable(IEnumerable<DrawingSheet> list) => new("Register",
        new[] { "Page", "Sheet", "Title", "Revision", "Date", "Status" },
        list.Select(d => new object?[] { d.PageNumber, d.Sheet, d.Title, d.Revision, d.Date, d.Status }).ToList());

    public static TableData ZoneResultsTable(IEnumerable<ZoneResult> list) => new("ZoneCheck",
        new[] { "File", "Page", "Zone", "Rule", "Value PDF 1", "Value PDF 2", "Result", "Message" },
        list.Select(r => new object?[] { r.File, r.PageNumber, r.Zone, r.Rule, r.Value, r.ValueB, r.Result, r.Message }).ToList());

    public static TableData ChangesTable(IEnumerable<ChangeRegion> list) => new("Changes",
        new[] { "#", "Page", "Kind", "Description", "X", "Y", "Width", "Height" },
        list.Select((c, i) => new object?[] { i + 1, c.PageNumber, c.Kind, c.Description, Math.Round(c.Rect.X, 1), Math.Round(c.Rect.Y, 1), Math.Round(c.Rect.Width, 1), Math.Round(c.Rect.Height, 1) }).ToList());

    public static void ExportCsv(string path, TableData t)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", t.Headers.Select(Csv)));
        foreach (var r in t.Rows) sb.AppendLine(string.Join(",", r.Select(v => Csv(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""))));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM so Excel shows Vietnamese correctly
    }

    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static void ExportExcel(string path, params TableData[] tables)
    {
        using var wb = new XLWorkbook();
        foreach (var t in tables)
        {
            string name = new string(t.Name.Where(c => !"[]*?/\\:".Contains(c)).ToArray());
            if (name.Length > 31) name = name[..31];
            var ws = wb.Worksheets.Add(string.IsNullOrEmpty(name) ? "Sheet" : name);
            for (int c = 0; c < t.Headers.Length; c++) ws.Cell(1, c + 1).Value = t.Headers[c];
            var head = ws.Range(1, 1, 1, Math.Max(1, t.Headers.Length));
            head.Style.Font.Bold = true;
            head.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F3B57");
            head.Style.Font.FontColor = XLColor.White;
            for (int r = 0; r < t.Rows.Count; r++)
                for (int c = 0; c < t.Rows[r].Length; c++)
                {
                    var v = t.Rows[r][c];
                    var cell = ws.Cell(r + 2, c + 1);
                    switch (v)
                    {
                        case null: break;
                        case int i: cell.Value = i; break;
                        case double d: cell.Value = d; break;
                        case bool b: cell.Value = b; break;
                        default: cell.Value = v.ToString(); break;
                    }
                }
            ws.SheetView.FreezeRows(1);
            if (t.Rows.Count > 0) ws.Range(1, 1, t.Rows.Count + 1, t.Headers.Length).SetAutoFilter();
            ws.Columns().AdjustToContents(1, Math.Min(t.Rows.Count + 1, 300), 8, 60);
        }
        wb.SaveAs(path);
    }

    // ============================================================ raw PDF drawing
    /// <summary>Writes PDF content operators in DISPLAY coordinates. A "cm" maps display -> user space,
    /// so output is correct for any /Rotate and MediaBox origin (verified with PDFium).</summary>
    private sealed class RawPdfWriter
    {
        private readonly StringBuilder _sb = new();
        private readonly PdfDictionary _resources;
        private readonly SharpDoc _doc;
        private int _gsCounter;
        private const string FontName = "/JNNHelv";
        private const string FontBold = "/JNNHelvB";

        public RawPdfWriter(PdfPage page, Affine displayToUser)
        {
            _doc = page.Owner;
            _resources = page.Elements.GetDictionary("/Resources") ?? new PdfDictionary(_doc);
            if (page.Elements["/Resources"] == null) page.Elements["/Resources"] = _resources;
            EnsureFont(FontName, "Helvetica");
            EnsureFont(FontBold, "Helvetica-Bold");
            _sb.Append("Q q ");
            _sb.Append(F(displayToUser.A)).Append(' ').Append(F(displayToUser.B)).Append(' ')
               .Append(F(displayToUser.C)).Append(' ').Append(F(displayToUser.D)).Append(' ')
               .Append(F(displayToUser.E)).Append(' ').Append(F(displayToUser.F)).Append(" cm 1 J 1 j\n");
        }

        private void EnsureFont(string name, string baseFont)
        {
            var fonts = _resources.Elements.GetDictionary("/Font");
            if (fonts == null) { fonts = new PdfDictionary(_doc); _resources.Elements["/Font"] = fonts; }
            if (fonts.Elements[name] != null) return;
            var f = new PdfDictionary(_doc);
            f.Elements["/Type"] = new PdfName("/Font");
            f.Elements["/Subtype"] = new PdfName("/Type1");
            f.Elements["/BaseFont"] = new PdfName("/" + baseFont);
            f.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
            fonts.Elements[name] = f;
        }

        private string Gs(double fillAlpha, double strokeAlpha)
        {
            var gss = _resources.Elements.GetDictionary("/ExtGState");
            if (gss == null) { gss = new PdfDictionary(_doc); _resources.Elements["/ExtGState"] = gss; }
            string name = $"/JNNGS{_gsCounter++}_{Guid.NewGuid().ToString("N")[..6]}";
            var gs = new PdfDictionary(_doc);
            gs.Elements["/Type"] = new PdfName("/ExtGState");
            gs.Elements["/ca"] = new PdfReal(fillAlpha);
            gs.Elements["/CA"] = new PdfReal(strokeAlpha);
            gss.Elements[name] = gs;
            return name;
        }

        private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        private static (double R, double G, double B) Rgb(string hex)
        {
            try { var c = (Color)ColorConverter.ConvertFromString(hex); return (c.R / 255.0, c.G / 255.0, c.B / 255.0); }
            catch { return (1, 0, 0); }
        }

        public void Begin(string stroke, string? fill, double width, double alpha, bool multiplyLike = false)
        {
            var s = Rgb(stroke);
            _sb.Append("q ").Append($"{F(s.R)} {F(s.G)} {F(s.B)} RG {F(width)} w ");
            if (fill != null) { var f = Rgb(fill); _sb.Append($"{F(f.R)} {F(f.G)} {F(f.B)} rg "); }
            if (alpha < 0.999) _sb.Append(Gs(alpha, alpha)).Append(" gs ");
            _sb.Append('\n');
        }
        public void End() => _sb.Append("Q\n");

        public void Poly(IReadOnlyList<PdfPoint> pts, bool close, bool stroke = true, bool fill = false)
        {
            if (pts.Count < 2) return;
            _sb.Append($"{F(pts[0].X)} {F(pts[0].Y)} m ");
            for (int i = 1; i < pts.Count; i++) _sb.Append($"{F(pts[i].X)} {F(pts[i].Y)} l ");
            if (close) _sb.Append("h ");
            _sb.Append(fill && stroke ? "B\n" : fill ? "f\n" : "S\n");
        }

        public void Rect(PdfRect r, bool stroke = true, bool fill = false)
            => _sb.Append($"{F(r.X)} {F(r.Y)} {F(r.Width)} {F(r.Height)} re ").Append(fill && stroke ? "B\n" : fill ? "f\n" : "S\n");

        public void Ellipse(PdfRect r, bool stroke = true, bool fill = false)
        {
            double k = 0.5523, cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rx = r.Width / 2, ry = r.Height / 2;
            _sb.Append($"{F(cx + rx)} {F(cy)} m ");
            _sb.Append($"{F(cx + rx)} {F(cy + k * ry)} {F(cx + k * rx)} {F(cy + ry)} {F(cx)} {F(cy + ry)} c ");
            _sb.Append($"{F(cx - k * rx)} {F(cy + ry)} {F(cx - rx)} {F(cy + k * ry)} {F(cx - rx)} {F(cy)} c ");
            _sb.Append($"{F(cx - rx)} {F(cy - k * ry)} {F(cx - k * rx)} {F(cy - ry)} {F(cx)} {F(cy - ry)} c ");
            _sb.Append($"{F(cx + k * rx)} {F(cy - ry)} {F(cx + rx)} {F(cy - k * ry)} {F(cx + rx)} {F(cy)} c h ");
            _sb.Append(fill && stroke ? "B\n" : fill ? "f\n" : "S\n");
        }

        /// <summary>Single text line, top-left anchored, in display coordinates.</summary>
        public void Text(double x, double top, string text, double size, string color, bool bold = false)
        {
            var c = Rgb(color);
            _sb.Append($"BT {F(c.R)} {F(c.G)} {F(c.B)} rg {(bold ? FontBold : FontName)} {F(size)} Tf 1 0 0 -1 {F(x)} {F(top + size * 0.82)} Tm (")
               .Append(Escape(text)).Append(") Tj ET\n");
        }

        /// <summary>Approximate Helvetica text width (points).</summary>
        public static double TextWidth(string s, double size) => s.Length * size * 0.55;

        private static string Escape(string s)
        {
            var enc = Encoding.Latin1;
            var sb = new StringBuilder();
            foreach (char ch0 in s)
            {
                char ch = ch0;
                if (ch > 255) ch = Fold(ch);
                if (ch == '(' || ch == ')' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 32) sb.Append(' ');
                else if (ch > 127) sb.Append('\\').Append(Convert.ToString(ch, 8).PadLeft(3, '0'));
                else sb.Append(ch);
            }
            _ = enc;
            return sb.ToString();
        }

        /// <summary>Folds non WinAnsi chars (Vietnamese ơ, ư, ạ, ế...) to their base letter.</summary>
        private static char Fold(char ch)
        {
            if (ch == 'đ') return 'd';
            if (ch == 'Đ') return 'D';
            string d = ch.ToString().Normalize(NormalizationForm.FormD);
            foreach (char c in d)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) return c <= 255 ? c : '?';
            return '?';
        }

        public void Raw(string ops) => _sb.Append(ops).Append('\n');

        /// <summary>Wraps the existing page content in q..Q and appends our stream.</summary>
        public void Commit(PdfPage page)
        {
            var pre = page.Contents.PrependContent();
            pre.CreateStream(Encoding.ASCII.GetBytes("q\n"));
            var post = page.Contents.AppendContent();
            _sb.Append("Q\n");
            post.CreateStream(Encoding.ASCII.GetBytes(_sb.ToString()));
        }
    }

    /// <summary>Draws one markup with the raw writer (vector).</summary>
    private static void DrawMarkupPdf(RawPdfWriter w, Markup m)
    {
        var p = m.Points;
        double lw = m.LineWidth;
        string col = m.Color;
        switch (m.Type)
        {
            case MarkupType.Line:
            case MarkupType.Polyline:
            case MarkupType.Pen:
            case MarkupType.Length:
            case MarkupType.PolyLength:
                w.Begin(col, null, lw, m.Opacity); w.Poly(p, false); w.End(); break;
            case MarkupType.Highlighter:
                w.Begin(col, null, Math.Max(lw, 8), 0.4); w.Poly(p, false); w.End(); break;
            case MarkupType.Arrow:
                w.Begin(col, col, lw, m.Opacity); w.Poly(p, false);
                if (p.Count >= 2) w.Poly(ArrowHead(p[^2], p[^1], Math.Max(8, lw * 4)), true, true, true);
                w.End(); break;
            case MarkupType.Polygon:
            case MarkupType.Area:
            case MarkupType.Perimeter:
                w.Begin(col, m.FillColor ?? (m.Type == MarkupType.Area ? col : null), lw, m.Opacity);
                if (m.Type == MarkupType.Area)
                {
                    w.End(); w.Begin(col, col, lw, 0.18); w.Poly(p, true, false, true); w.End();
                    w.Begin(col, null, lw, m.Opacity); w.Poly(p, true); w.End();
                }
                else { w.Poly(p, true, true, m.FillColor != null); w.End(); }
                break;
            case MarkupType.Rectangle:
            case MarkupType.TextBox:
            case MarkupType.Hyperlink:
                if (p.Count < 2) break;
                var r = PdfRect.FromPoints(p[0], p[1]);
                if (m.FillColor != null) { w.Begin(col, m.FillColor, lw, Math.Min(m.Opacity, 0.35)); w.Rect(r, false, true); w.End(); }
                w.Begin(col, null, lw, m.Opacity); w.Rect(r); w.End();
                if (m.Type == MarkupType.TextBox) DrawTextBlock(w, r, m.Text, m.FontSize, col);
                break;
            case MarkupType.Typewriter:
                if (p.Count < 1) break;
                DrawTextBlock(w, new PdfRect(p[0].X, p[0].Y, 2000, 2000), m.Text, m.FontSize, col); break;
            case MarkupType.Label:
                if (p.Count < 2) break;
                var lr = PdfRect.FromPoints(p[0], p[1]);
                w.Begin(col, "#FFFFFF", lw, 1); w.Rect(lr, true, true); w.End();
                DrawTextBlock(w, lr.Inflate(-2), m.Text, m.FontSize, col); break;
            case MarkupType.Ellipse:
                if (p.Count < 2) break;
                w.Begin(col, m.FillColor, lw, m.Opacity); w.Ellipse(PdfRect.FromPoints(p[0], p[1]), true, m.FillColor != null); w.End(); break;
            case MarkupType.Cloud:
            case MarkupType.Tag:
                if (p.Count < 2) break;
                var cr = PdfRect.FromPoints(p[0], p[1]);
                w.Begin(col, null, lw, m.Opacity);
                w.Poly(Geometry.CloudPoints(Geometry.RectCorners(cr), Math.Clamp(Math.Min(cr.Width, cr.Height) / 6, 6, 30)), true); w.End();
                if (m.Type == MarkupType.Tag)
                {
                    string tag = string.IsNullOrEmpty(m.Subject) ? "TAG" : m.Subject;
                    double fs = m.FontSize, tw = RawPdfWriter.TextWidth(tag, fs) + 8;
                    var tr = new PdfRect(cr.X, cr.Y - fs - 8, tw, fs + 6);
                    w.Begin(col, col, 1, 1); w.Rect(tr, true, true); w.End();
                    w.Text(tr.X + 4, tr.Y + 3, tag, fs, "#FFFFFF", true);
                    if (!string.IsNullOrWhiteSpace(m.Text)) w.Text(tr.Right + 4, tr.Y + 3, m.Text, fs, col);
                }
                break;
            case MarkupType.Callout:
                if (p.Count < 3) break;
                var box = PdfRect.FromPoints(p[1], p[2]);
                w.Begin(col, col, lw, m.Opacity);
                w.Poly(new[] { p[0], new PdfPoint(box.X + box.Width / 2, box.Y + box.Height / 2) }, false);
                w.Poly(ArrowHead(new PdfPoint(box.X + box.Width / 2, box.Y + box.Height / 2), p[0], Math.Max(8, lw * 4)), true, true, true);
                w.End();
                w.Begin(col, "#FFFFFF", lw, 1); w.Rect(box, true, true); w.End();
                DrawTextBlock(w, box.Inflate(-3), m.Text, m.FontSize, col); break;
            case MarkupType.Highlight:
                w.Begin(col, col, 0, 0.35);
                foreach (var hr in m.Rects ?? (p.Count >= 2 ? new List<PdfRect> { PdfRect.FromPoints(p[0], p[1]) } : new List<PdfRect>()))
                    w.Rect(hr, false, true);
                w.End(); break;
            case MarkupType.Stamp:
                if (p.Count < 2) break;
                var sr = PdfRect.FromPoints(p[0], p[1]);
                w.Begin(col, null, Math.Max(2, lw), 0.9); w.Rect(sr); w.Rect(sr.Inflate(-3)); w.End();
                double sfs = Math.Clamp(sr.Height * 0.32, 6, 80);
                w.Text(sr.X + 8, sr.Y + 6, m.Subject, sfs, col, true);
                w.Text(sr.X + 8, sr.Y + 8 + sfs, $"{m.CreatedDate:dd/MM/yyyy}  {m.Author}  {(string.IsNullOrEmpty(m.Revision) ? "" : "Rev " + m.Revision)}", sfs * 0.45, col);
                break;
            case MarkupType.IssuePin:
                if (p.Count < 1) break;
                var pin = new PdfRect(p[0].X - 9, p[0].Y - 9, 18, 18);
                w.Begin(col, col, 1, 1); w.Ellipse(pin, true, true); w.End();
                w.Text(pin.Right + 3, pin.Y, m.Subject, 10, col, true); break;
            case MarkupType.Count:
                w.Begin(col, col, 1, 0.9);
                for (int i = 0; i < p.Count; i++) w.Ellipse(new PdfRect(p[i].X - 5, p[i].Y - 5, 10, 10), true, true);
                w.End();
                for (int i = 0; i < p.Count; i++) w.Text(p[i].X + 6, p[i].Y - 12, (i + 1).ToString(), 8, col);
                break;
            case MarkupType.Angle:
                w.Begin(col, null, lw, m.Opacity); w.Poly(p, false); w.End(); break;
        }
        // measurement label
        if (m.IsMeasurement && p.Count > 0 && !string.IsNullOrEmpty(m.ValueText))
        {
            var b = m.Bounds;
            double fs = Math.Max(m.FontSize, 9);
            var anchor = m.Type is MarkupType.Area or MarkupType.Perimeter ? new PdfPoint(b.X + b.Width / 2 - RawPdfWriter.TextWidth(m.ValueText, fs) / 2, b.Y + b.Height / 2 - fs / 2)
                : new PdfPoint(p[^1].X + 6, p[^1].Y - fs - 4);
            var lr = new PdfRect(anchor.X - 2, anchor.Y - 2, RawPdfWriter.TextWidth(m.ValueText, fs) + 4, fs + 4);
            w.Begin("#FFFFFF", "#FFFFFF", 0, 0.85); w.Rect(lr, false, true); w.End();
            w.Text(anchor.X, anchor.Y, m.ValueText, fs, m.Color, true);
        }
    }

    private static void DrawTextBlock(RawPdfWriter w, PdfRect r, string text, double size, string color)
    {
        double y = r.Y + 2;
        foreach (var line in Wrap(text ?? "", Math.Max(20, r.Width - 4), size))
        {
            if (y + size > r.Bottom + size * 0.2) break;
            w.Text(r.X + 3, y, line, size, color);
            y += size * 1.2;
        }
    }

    public static IEnumerable<string> Wrap(string text, double width, double size)
    {
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var words = para.Split(' ');
            var line = "";
            foreach (var wd in words)
            {
                string cand = line.Length == 0 ? wd : line + " " + wd;
                if (RawPdfWriter.TextWidth(cand, size) > width && line.Length > 0) { yield return line; line = wd; }
                else line = cand;
            }
            yield return line;
        }
    }

    public static List<PdfPoint> ArrowHead(PdfPoint from, PdfPoint to, double size)
    {
        double ang = Math.Atan2(to.Y - from.Y, to.X - from.X);
        return new List<PdfPoint>
        {
            to,
            new(to.X - size * Math.Cos(ang - 0.4), to.Y - size * Math.Sin(ang - 0.4)),
            new(to.X - size * Math.Cos(ang + 0.4), to.Y - size * Math.Sin(ang + 0.4))
        };
    }

    // ============================================================ PDF outputs
    /// <summary>Copies the source PDF and flattens markups (and optional change clouds) into the pages.</summary>
    public static void ExportFlattenedPdf(JnnPdf.PdfDocument src, string outPath, IEnumerable<Markup> markups,
        Func<int, ScaleSetting> scaleOf, IEnumerable<ChangeRegion>? changes = null, IProgress<int>? progress = null)
    {
        string tmp = outPath + ".tmp";
        File.Copy(src.FilePath, tmp, true);
        try
        {
            using (var doc = PdfReader.Open(tmp, PdfDocumentOpenMode.Modify))
            {
                var byPage = markups.GroupBy(m => m.Page).ToDictionary(g => g.Key, g => g.ToList());
                var chByPage = (changes ?? Enumerable.Empty<ChangeRegion>()).GroupBy(c => c.Page).ToDictionary(g => g.Key, g => g.ToList());
                for (int i = 0; i < doc.PageCount; i++)
                {
                    bool hasM = byPage.TryGetValue(i, out var ms), hasC = chByPage.TryGetValue(i, out var cs);
                    if (!hasM && !hasC) continue;
                    var page = doc.Pages[i];
                    var w = new RawPdfWriter(page, src.GetUserToDisplay(i).Invert());
                    foreach (var m in ms ?? new List<Markup>())
                    {
                        MeasurementService.Update(m, scaleOf(i));
                        DrawMarkupPdf(w, m);
                    }
                    foreach (var c in cs ?? new List<ChangeRegion>())
                    {
                        string col = c.Kind.StartsWith("Added") || c.Kind == "Text added" ? "#2E7D32" : c.Kind.Contains("Removed") || c.Kind == "Text removed" ? "#C62828" : "#EF6C00";
                        w.Begin(col, null, 1.5, 0.9);
                        w.Poly(Geometry.CloudPoints(Geometry.RectCorners(c.Rect.Inflate(4)), Math.Clamp(Math.Min(c.Rect.Width, c.Rect.Height) / 5, 4, 20)), true);
                        w.End();
                    }
                    w.Commit(page);
                    progress?.Report(i + 1);
                }
                doc.Save(outPath);
            }
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>Crops one page to a display rectangle (vector, via CropBox/MediaBox).</summary>
    public static void CropToPdf(JnnPdf.PdfDocument src, int pageIndex, PdfRect rect, string outPath)
    {
        var inv = src.GetUserToDisplay(pageIndex).Invert();
        var a = inv.Apply(rect.X, rect.Y); var b = inv.Apply(rect.Right, rect.Bottom);
        using var input = PdfReader.Open(src.FilePath, PdfDocumentOpenMode.Import);
        using var output = new PdfSharp.Pdf.PdfDocument();
        var page = output.AddPage(input.Pages[pageIndex]);
        var box = new PdfRectangle(new XPoint(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new XPoint(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
        page.MediaBox = box;
        page.CropBox = box;
        output.Save(outPath);
    }

    /// <summary>Page operations: builds a new PDF from an ordered list of (source page, extra rotation).</summary>
    public static void RebuildPdf(string srcPath, IReadOnlyList<(int Page, int Rotate)> order, string outPath)
    {
        var inputs = new List<PdfSharp.Pdf.PdfDocument>();
        try
        {
            using var output = new PdfSharp.Pdf.PdfDocument();
            var usage = new Dictionary<int, int>();
            foreach (var (pg, rot) in order)
            {
                usage.TryGetValue(pg, out int n);
                usage[pg] = n + 1;
                while (inputs.Count <= n) inputs.Add(PdfReader.Open(srcPath, PdfDocumentOpenMode.Import)); // duplicates need fresh import
                var page = output.AddPage(inputs[n].Pages[pg]);
                if (rot != 0) page.Rotate = ((page.Rotate + rot) % 360 + 360) % 360;
            }
            output.Save(outPath);
        }
        finally { foreach (var d in inputs) d.Dispose(); }
    }

    public static void MergePdfs(IEnumerable<string> files, string outPath, CancellationToken ct, IProgress<string>? progress = null)
    {
        using var output = new PdfSharp.Pdf.PdfDocument();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(Path.GetFileName(f));
            using var input = PdfReader.Open(f, PdfDocumentOpenMode.Import);
            foreach (var p in input.Pages) output.AddPage(p);
        }
        output.Save(outPath);
    }

    /// <summary>Adds a stamp (text, date, author, revision) to the top-right of every page.</summary>
    public static void StampPdf(string srcPath, string outPath, string stampText, string author, string revision, string color = "#C62828")
    {
        using var probe = JnnPdf.PdfDocument.Open(srcPath);
        File.Copy(srcPath, outPath + ".tmp", true);
        try
        {
            using (var doc = PdfReader.Open(outPath + ".tmp", PdfDocumentOpenMode.Modify))
            {
                for (int i = 0; i < doc.PageCount; i++)
                {
                    var size = probe.GetPageSize(i);
                    double h = Math.Clamp(size.Height * 0.05, 40, 120), wdt = h * 4.2;
                    var m = new Markup
                    {
                        Type = MarkupType.Stamp, Subject = stampText, Author = author, Revision = revision, Color = color,
                        Points = new List<PdfPoint> { new(size.Width - wdt - 30, 30), new(size.Width - 30, 30 + h) }
                    };
                    var w = new RawPdfWriter(doc.Pages[i], probe.GetUserToDisplay(i).Invert());
                    DrawMarkupPdf(w, m);
                    w.Commit(doc.Pages[i]);
                }
                doc.Save(outPath);
            }
        }
        finally { try { File.Delete(outPath + ".tmp"); } catch { } }
    }

    /// <summary>Makes a scanned PDF searchable: OCR words drawn as invisible text (render mode 3).</summary>
    public static void ExportSearchablePdf(JnnPdf.PdfDocument src, Dictionary<int, List<OcrWord>> ocr, string outPath)
    {
        File.Copy(src.FilePath, outPath + ".tmp", true);
        try
        {
            using (var doc = PdfReader.Open(outPath + ".tmp", PdfDocumentOpenMode.Modify))
            {
                foreach (var (page, words) in ocr)
                {
                    if (page >= doc.PageCount || words.Count == 0) continue;
                    var w = new RawPdfWriter(doc.Pages[page], src.GetUserToDisplay(page).Invert());
                    w.Raw("3 Tr");
                    foreach (var word in words) w.Text(word.Rect.X, word.Rect.Y, word.Text, Math.Max(4, word.Rect.Height * 0.9), "#000000");
                    w.Commit(doc.Pages[page]);
                }
                doc.Save(outPath);
            }
        }
        finally { try { File.Delete(outPath + ".tmp"); } catch { } }
    }

    // ============================================================ report
    public sealed record ReportInfo(string Project, string Drawing, string Revision, string Reviewer, string Summary);

    /// <summary>Professional PDF report: cover/summary + tables.</summary>
    public static void ExportReportPdf(string outPath, ReportInfo info, params TableData[] tables)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        doc.Info.Title = "JNN PDF Review Report";
        var fTitle = new XFont("Arial", 18, XFontStyleEx.Bold);
        var fHead = new XFont("Arial", 11, XFontStyleEx.Bold);
        var fCell = new XFont("Arial", 7.5, XFontStyleEx.Regular);
        var fCellB = new XFont("Arial", 7.5, XFontStyleEx.Bold);
        var accent = XColor.FromArgb(31, 59, 87);

        PdfPage page = doc.AddPage(); page.Size = PdfSharp.PageSize.A4; page.Orientation = PdfSharp.PageOrientation.Landscape;
        XGraphics gfx = XGraphics.FromPdfPage(page);
        double W = page.Width.Point, H = page.Height.Point, margin = 36, y = margin;

        void NewPage()
        {
            gfx.Dispose();
            page = doc.AddPage(); page.Size = PdfSharp.PageSize.A4; page.Orientation = PdfSharp.PageOrientation.Landscape;
            gfx = XGraphics.FromPdfPage(page);
            y = margin;
            gfx.DrawString($"JNN PDF – {info.Project}", fCell, XBrushes.Gray, new XPoint(margin, H - 18));
            gfx.DrawString($"Page {doc.PageCount}", fCell, XBrushes.Gray, new XPoint(W - margin - 40, H - 18));
        }

        // header
        gfx.DrawRectangle(new XSolidBrush(accent), 0, 0, W, 64);
        gfx.DrawString("JNN PDF – REVIEW REPORT", fTitle, XBrushes.White, new XPoint(margin, 40));
        y = 90;
        var rowsInfo = new (string, string)[]
        {
            ("Project", info.Project), ("Drawing", info.Drawing), ("Revision", info.Revision),
            ("Reviewer", info.Reviewer), ("Date", DateTime.Now.ToString("dd/MM/yyyy HH:mm")), ("Summary", info.Summary)
        };
        foreach (var (k, v) in rowsInfo)
        {
            gfx.DrawString(k + ":", fHead, new XSolidBrush(accent), new XPoint(margin, y));
            double yy = y;
            foreach (var line in WrapX(gfx, v ?? "", fHead, W - margin * 2 - 110)) { gfx.DrawString(line, new XFont("Arial", 10), XBrushes.Black, new XPoint(margin + 110, yy)); yy += 14; }
            y = Math.Max(y + 18, yy + 4);
        }
        y += 10;

        foreach (var t in tables)
        {
            if (y > H - 120) NewPage();
            gfx.DrawString($"{t.Name}  ({t.Rows.Count})", fHead, new XSolidBrush(accent), new XPoint(margin, y)); y += 8;
            if (t.Rows.Count == 0) { gfx.DrawString("(none)", fCell, XBrushes.Gray, new XPoint(margin, y + 12)); y += 30; continue; }
            // column widths proportional to content length (capped)
            int nc = t.Headers.Length;
            var weights = new double[nc];
            for (int c = 0; c < nc; c++)
                weights[c] = Math.Clamp(Math.Max(t.Headers[c].Length, t.Rows.Take(50).Select(r => (Convert.ToString(r[c]) ?? "").Length).DefaultIfEmpty(0).Max()), 3, 40);
            double sum = weights.Sum(), avail = W - margin * 2;
            var cw = weights.Select(x => x / sum * avail).ToArray();

            void Header()
            {
                double x = margin;
                gfx.DrawRectangle(new XSolidBrush(accent), margin, y, avail, 14);
                for (int c = 0; c < nc; c++) { gfx.DrawString(Clip(gfx, t.Headers[c], fCellB, cw[c] - 4), fCellB, XBrushes.White, new XPoint(x + 2, y + 10)); x += cw[c]; }
                y += 14;
            }
            Header();
            int ri = 0;
            foreach (var r in t.Rows)
            {
                if (y > H - 40) { NewPage(); Header(); }
                if (ri++ % 2 == 1) gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(240, 244, 248)), margin, y, avail, 12);
                double x = margin;
                for (int c = 0; c < nc; c++)
                {
                    gfx.DrawString(Clip(gfx, Convert.ToString(r[c], CultureInfo.InvariantCulture) ?? "", fCell, cw[c] - 4), fCell, XBrushes.Black, new XPoint(x + 2, y + 9));
                    x += cw[c];
                }
                y += 12;
            }
            y += 18;
        }
        gfx.Dispose();
        doc.Save(outPath);
    }

    private static string Clip(XGraphics g, string s, XFont f, double width)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        if (g.MeasureString(s, f).Width <= width) return s;
        while (s.Length > 1 && g.MeasureString(s + "…", f).Width > width) s = s[..^1];
        return s + "…";
    }

    private static IEnumerable<string> WrapX(XGraphics g, string text, XFont f, double width)
    {
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            string line = "";
            foreach (var wd in para.Split(' '))
            {
                string cand = line.Length == 0 ? wd : line + " " + wd;
                if (g.MeasureString(cand, f).Width > width && line.Length > 0) { yield return line; line = wd; }
                else line = cand;
            }
            yield return line;
        }
    }

    // ============================================================ raster outputs
    /// <summary>Renders a page with markups to a bitmap (UI thread). dpi: output resolution.</summary>
    public static BitmapSource RenderPageWithMarkups(JnnPdf.PdfDocument doc, int page, double dpi, IEnumerable<Markup> markups, PdfRect? clip = null)
    {
        var size = doc.GetPageSize(page);
        var area = clip ?? new PdfRect(0, 0, size.Width, size.Height);
        double scale = dpi / 72.0;
        var bmp = doc.Render(page, scale, new Rect(area.X, area.Y, area.Width, area.Height));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(bmp, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, -area.X * scale, -area.Y * scale));
            foreach (var m in markups.Where(m => m.Page == page)) MarkupRenderer.Draw(dc, m, scale, false);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(bmp.PixelWidth, bmp.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    public static void SavePng(BitmapSource bmp, string path)
    {
        BitmapEncoder enc = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Prints pages (with markups) using the Windows print dialog.</summary>
    public static void Print(JnnPdf.PdfDocument doc, IList<Markup> markups, int currentPage)
    {
        var dlg = new PrintDialog { UserPageRangeEnabled = true, MinPage = 1, MaxPage = (uint)doc.PageCount, PageRange = new PageRange(currentPage + 1) };
        if (dlg.ShowDialog() != true) return;
        int from = 0, to = doc.PageCount - 1;
        if (dlg.PageRangeSelection == PageRangeSelection.UserPages) { from = Math.Max(0, dlg.PageRange.PageFrom - 1); to = Math.Min(doc.PageCount - 1, dlg.PageRange.PageTo - 1); }
        else if (dlg.PageRangeSelection == PageRangeSelection.CurrentPage) from = to = currentPage;
        var paginator = new PagePaginator(doc, markups, from, to, new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight));
        dlg.PrintDocument(paginator, "JNN PDF - " + Path.GetFileName(doc.FilePath));
    }

    private sealed class PagePaginator : DocumentPaginator
    {
        private readonly JnnPdf.PdfDocument _doc; private readonly IList<Markup> _markups; private readonly int _from, _to;
        public PagePaginator(JnnPdf.PdfDocument d, IList<Markup> m, int from, int to, Size size) { _doc = d; _markups = m; _from = from; _to = to; PageSize = size; }
        public override bool IsPageCountValid => true;
        public override int PageCount => _to - _from + 1;
        public override Size PageSize { get; set; }
        public override IDocumentPaginatorSource? Source => null;
        public override DocumentPage GetPage(int pageNumber)
        {
            int p = _from + pageNumber;
            var size = _doc.GetPageSize(p);
            double fit = Math.Min(PageSize.Width / size.Width, PageSize.Height / size.Height);
            var bmp = _doc.Render(p, Math.Min(4, fit * 2)); // ~ 2x printer DIP resolution
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new MatrixTransform(fit, 0, 0, fit, 0, 0));
                dc.DrawImage(bmp, new Rect(0, 0, size.Width, size.Height));
                foreach (var m in _markups.Where(m => m.Page == p)) MarkupRenderer.Draw(dc, m, fit, false);
                dc.Pop();
            }
            return new DocumentPage(dv, PageSize, new Rect(PageSize), new Rect(PageSize));
        }
    }
}
