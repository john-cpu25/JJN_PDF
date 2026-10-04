// ============================================================================
//  App.xaml.cs  –  APPLICATION BOOTSTRAP
//  * Global exception handling + logging (the app must never crash silently)
//  * Dark / Light theme switching
//  * XAML value converters
//  * --selftest <pdf> [pdf2] : headless verification of the engine
// ============================================================================
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace JnnPdf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Logger.Error("Unhandled (domain)", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Logger.Error("Unobserved task", a.Exception); a.SetObserved(); };
        Logger.Info($"=== JNN PDF start {string.Join(" ", e.Args)} ===");

        if (e.Args.Length > 0 && e.Args[0] == "--selftest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = SelfTest.Run(e.Args.Skip(1).ToArray());
            Shutdown(code);
            return;
        }
        base.OnStartup(e);
        var db = new DatabaseService();
        bool dark = db.Get("theme", "dark") != "light";
        // dev/QA: --snapshot <out.png> [pdf] [pdf2] [light|dark] renders the main window to PNG and exits
        bool snapshot = e.Args.Length > 1 && e.Args[0] == "--snapshot";
        if (snapshot && e.Args.Length > 4) dark = e.Args[4] != "light";
        ApplyTheme(dark);
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
        if (snapshot) _ = w.SnapshotAsync(e.Args[1], e.Args.ElementAtOrDefault(2), e.Args.ElementAtOrDefault(3));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ProjectService.EndSession(); // clean exit -> no crash recovery prompt next time
        Logger.Info("=== JNN PDF exit ===");
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show($"An error occurred, but the application keeps running:\n\n{e.Exception.Message}\n\nDetails in the log: {AppPaths.Logs}",
            "JNN PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    /// <summary>Swaps the theme brushes (DynamicResource keys) for dark or light mode.</summary>
    public static void ApplyTheme(bool dark)
    {
        var r = Current.Resources;
        void S(string key, string hex) => r[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        if (dark)
        {
            S("WindowBrush", "#1E2227"); S("PanelBrush", "#252A31"); S("PanelAltBrush", "#2D333B"); S("HeaderBrush", "#20252B");
            S("LineBrush", "#3A414B"); S("TextBrush", "#E6E9EE"); S("SubTextBrush", "#9AA4B2"); S("AccentBrush", "#3D8BFD");
            S("HoverBrush", "#343B45"); S("InputBrush", "#1B1F24"); S("CanvasBrush", "#2B2F36"); S("SelectionBrush", "#2F4F7A");
        }
        else
        {
            S("WindowBrush", "#E9ECF0"); S("PanelBrush", "#FFFFFF"); S("PanelAltBrush", "#F5F7FA"); S("HeaderBrush", "#F0F2F5");
            S("LineBrush", "#D5D9E0"); S("TextBrush", "#1F2328"); S("SubTextBrush", "#5F6B7A"); S("AccentBrush", "#1F6FEB");
            S("HoverBrush", "#E6EEF9"); S("InputBrush", "#FFFFFF"); S("CanvasBrush", "#868D96"); S("SelectionBrush", "#CFE1FB");
        }
    }
}

// ----------------------------------------------------------------------------
//  Converters
// ----------------------------------------------------------------------------
/// <summary>Enum value == ConverterParameter (for tool radio buttons / overlay modes).</summary>
public sealed class EnumBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) => value?.ToString() == parameter?.ToString();
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c)
        => value is true && parameter != null ? Enum.Parse(t, parameter.ToString()!) : Binding.DoNothing;
}

public sealed class BoolVisConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object parameter, CultureInfo c) => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => (value is Visibility.Visible) ^ Invert;
}

public sealed class NullVisConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) => value == null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
}

// ----------------------------------------------------------------------------
//  Headless self test:  JnnPdf.exe --selftest file.pdf [file2.pdf] [outdir]
// ----------------------------------------------------------------------------
public static class SelfTest
{
    public static int Run(string[] args)
    {
        var log = new StringBuilder();
        void L(string s) { log.AppendLine(s); }
        string outDir = args.Length > 2 ? args[2] : Path.Combine(AppPaths.Root, "selftest");
        Directory.CreateDirectory(outDir);
        int fails = 0;
        void Check(bool ok, string what) { L((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
        try
        {
            string a = args.Length > 0 ? args[0] : throw new ArgumentException("missing pdf");
            using var doc = PdfDocument.Open(a);
            Check(doc.PageCount > 0, $"open {Path.GetFileName(a)} pages={doc.PageCount}");
            var s = doc.GetPageSize(0);
            L($"page0 display size {s.Width:0.#} x {s.Height:0.#}");
            var aff = doc.GetUserToDisplay(0);
            L($"affine user->display {aff}");
            var bmp = doc.Render(0, 800 / Math.Max(s.Width, s.Height));
            Check(bmp.PixelWidth > 100, $"render {bmp.PixelWidth}x{bmp.PixelHeight}");
            ExportService.SavePng(bmp, Path.Combine(outDir, "render.png"));
            var words = doc.GetWords(0);
            Check(words.Count > 0, $"words page0 = {words.Count}; first: {string.Join(" ", words.Take(8).Select(w => w.Text))}");
            var w0 = words.FirstOrDefault(w => w.Text.Length > 3);
            if (w0 != null)
            {
                var hits = doc.Search(w0.Text, CancellationToken.None);
                Check(hits.Count > 0, $"search '{w0.Text}' hits={hits.Count}");
                string inRect = doc.GetTextInRect(0, w0.Rect.Inflate(1));
                Check(inRect.Contains(w0.Text), $"region read '{inRect}'");
            }
            // Partial word: a region over only the last part of a long word must return that part.
            var longW = words.Where(w => w.Text.Length >= 10 && w.Glyphs is { Count: > 0 }).OrderByDescending(w => w.Text.Length).FirstOrDefault();
            if (longW != null)
            {
                var g = longW.Glyphs!;
                int from = g.Count - 6;
                var gr = g[from].Rect; for (int k = from + 1; k < g.Count; k++) gr = gr.Union(g[k].Rect);
                string tail = string.Concat(g.Skip(from).Select(x => x.Text));
                string part = doc.GetTextInRect(0, gr.Inflate(0.5));
                Check(part.Contains(tail) && part.Length < longW.Text.Length, $"partial region '{longW.Text}' -> '{part}' (expect '{tail}')");
            }
            // Flatten: red filled rectangle at a known display position, verify with PDFium pixel probe.
            var rect = new PdfRect(s.Width * 0.1, s.Height * 0.1, s.Width * 0.1, s.Height * 0.08);
            var m = new Markup { Type = MarkupType.Rectangle, Page = 0, Color = "#FF0000", FillColor = "#FF0000", Opacity = 1, LineWidth = 4,
                Points = new() { new(rect.X, rect.Y), new(rect.Right, rect.Bottom) } };
            var tm = new Markup { Type = MarkupType.TextBox, Page = 0, Color = "#0000FF", Text = "JNN test Việt Nam", FontSize = 30,
                Points = new() { new(s.Width * 0.3, s.Height * 0.1), new(s.Width * 0.6, s.Height * 0.2) } };
            var lm = new Markup { Type = MarkupType.Length, Page = 0, Color = "#00AA00", Points = new() { new(100, 100), new(100 + 72 / 25.4 * 50, 100) } };
            MeasurementService.Update(lm, ScaleSetting.Ratio(100));
            Check(lm.ValueText == "Length = 5.00 m", $"length 50mm @1:100 -> {lm.ValueText}");
            string flat = Path.Combine(outDir, "flatten.pdf");
            ExportService.ExportFlattenedPdf(doc, flat, new[] { m, tm, lm }, _ => ScaleSetting.Ratio(100));
            using (var f = PdfDocument.Open(flat))
            {
                f.RenderAnnotations = false;
                double sc = 1000 / Math.Max(s.Width, s.Height);
                var fb = f.Render(0, sc);
                ExportService.SavePng(fb, Path.Combine(outDir, "flatten.png"));
                var px = new byte[4];
                int cx = (int)((rect.X + rect.Width / 2) * sc), cy = (int)((rect.Y + rect.Height / 2) * sc);
                fb.CopyPixels(new System.Windows.Int32Rect(cx, cy, 1, 1), px, 4, 0);
                Check(px[2] > px[1] + 60 && px[2] > px[0] + 60, $"flatten position check pixel BGRA=({px[0]},{px[1]},{px[2]},{px[3]})");
            }
            // Crop
            string crop = Path.Combine(outDir, "crop.pdf");
            ExportService.CropToPdf(doc, 0, rect.Inflate(50), crop);
            using (var c = PdfDocument.Open(crop))
            {
                var cs = c.GetPageSize(0);
                Check(Math.Abs(cs.Width - (rect.Width + 100)) < 2 && Math.Abs(cs.Height - (rect.Height + 100)) < 2, $"crop size {cs.Width:0.#}x{cs.Height:0.#}");
                ExportService.SavePng(c.Render(0, 2), Path.Combine(outDir, "crop.png"));
            }
            // Project roundtrip
            var p = new ProjectData { PdfPath = a, Markups = { m, tm, lm }, Issues = { new Issue { IssueId = "ISS-001", Description = "test" } } };
            string pj = Path.Combine(outDir, "test" + ProjectService.Extension);
            ProjectService.Save(pj, p);
            var p2 = ProjectService.Load(pj);
            Check(p2.Markups.Count == 3 && p2.Issues.Count == 1 && File.Exists(p2.PdfPath), "project save/load roundtrip");
            // Excel + report
            ExportService.ExportExcel(Path.Combine(outDir, "markups.xlsx"), ExportService.MarkupsTable(p2.Markups), ExportService.IssuesTable(p2.Issues));
            ExportService.ExportReportPdf(Path.Combine(outDir, "report.pdf"), new ExportService.ReportInfo("Test", "A", "B", "me", "summary"),
                ExportService.MarkupsTable(p2.Markups), ExportService.IssuesTable(p2.Issues));
            Check(File.Exists(Path.Combine(outDir, "report.pdf")), "report pdf + excel");
            // Page ops
            string rebuilt = Path.Combine(outDir, "rebuilt.pdf");
            ExportService.RebuildPdf(a, new List<(int, int)> { (0, 90), (0, 0) }, rebuilt);
            using (var rb = PdfDocument.Open(rebuilt)) Check(rb.PageCount == 2 && Math.Abs(rb.GetPageSize(0).Width - s.Height) < 1, $"rebuild (rotate+duplicate) pages={rb.PageCount}");
            // Compare
            if (args.Length > 1 && File.Exists(args[1]))
            {
                using var b = PdfDocument.Open(args[1]);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ch = CompareService.ComparePage(doc, 0, b, 0, CancellationToken.None);
                Check(ch.Count > 0, $"compare page0 changes={ch.Count} in {sw.ElapsedMilliseconds} ms; first: {ch.FirstOrDefault()?.Description}");
                ExportService.ExportFlattenedPdf(b, Path.Combine(outDir, "compare.pdf"), Array.Empty<Markup>(), _ => ScaleSetting.Ratio(100), ch);
            }
            // Zones
            if (w0 != null)
            {
                var z = new Zone { Name = "Z", Rect = w0.Rect.Inflate(1), Rule = "contains", Expected = w0.Text, AllPages = false };
                var zr = ZoneService.Run(new[] { z }, doc, null, "a", CancellationToken.None);
                Check(zr.Count == 1 && zr[0].Passed, $"zone check: {zr.FirstOrDefault()?.Message}");
            }
        }
        catch (Exception ex) { L("EXCEPTION " + ex); fails++; }
        L(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllText(Path.Combine(outDir, "selftest.log"), log.ToString());
        return fails == 0 ? 0 : 1;
    }
}
