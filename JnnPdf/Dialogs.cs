// ============================================================================
//  Dialogs.cs  –  SMALL UI DIALOGS built in code (prompt, choice, scale,
//  calibrate, report info, batch processing window) + file pickers.
//  Kept in code so the prototype stays with very few XAML files.
// ============================================================================
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace JnnPdf;

public static class Dialogs
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    private static Window MakeWindow(string title, double width, double height = double.NaN)
    {
        var w = new Window
        {
            Title = title, Width = width, SizeToContent = double.IsNaN(height) ? SizeToContent.Height : SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar = false
        };
        if (!double.IsNaN(height)) w.Height = height;
        w.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        w.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        if (Owner != null && Owner != w) w.Owner = Owner;
        return w;
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 6, 0, 3), TextWrapping = TextWrapping.Wrap };

    private static StackPanel Buttons(Window w, Func<bool>? onOk = null, string ok = "OK")
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var bOk = new Button { Content = ok, MinWidth = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        bOk.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        var bCancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        bOk.Click += (_, _) => { if (onOk == null || onOk()) w.DialogResult = true; };
        sp.Children.Add(bOk); sp.Children.Add(bCancel);
        return sp;
    }

    // ------------------------------------------------------------ messages
    public static void Error(string msg, string title = "JNN PDF – Error")
        => MessageBox.Show(Owner!, msg, title, MessageBoxButton.OK, MessageBoxImage.Error);
    public static void Info(string msg, string title = "JNN PDF")
        => MessageBox.Show(Owner!, msg, title, MessageBoxButton.OK, MessageBoxImage.Information);
    public static bool Confirm(string msg, string title = "JNN PDF")
        => MessageBox.Show(Owner!, msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public static MessageBoxResult YesNoCancel(string msg, string title = "JNN PDF")
        => MessageBox.Show(Owner!, msg, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

    // ------------------------------------------------------------ prompts
    public static string? Prompt(string title, string label, string def = "", bool multiline = false, IEnumerable<string>? suggestions = null)
    {
        var w = MakeWindow(title, multiline ? 460 : 400);
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Label(label));
        Control input;
        if (suggestions != null)
        {
            var cb = new ComboBox { IsEditable = true, Text = def, ItemsSource = suggestions.ToList() };
            input = cb;
        }
        else
        {
            input = new TextBox
            {
                Text = def, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                Height = multiline ? 110 : double.NaN, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
            };
        }
        sp.Children.Add(input);
        sp.Children.Add(Buttons(w));
        w.Content = sp;
        w.Loaded += (_, _) => { input.Focus(); if (input is TextBox tb) tb.SelectAll(); };
        if (w.ShowDialog() != true) return null;
        return input is ComboBox c ? c.Text : ((TextBox)input).Text;
    }

    public static string? Password(string file)
    {
        var w = MakeWindow("Password-protected PDF", 380);
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Label($"Enter the password to open:\n{Path.GetFileName(file)}"));
        var pb = new PasswordBox();
        sp.Children.Add(pb);
        sp.Children.Add(Buttons(w));
        w.Content = sp;
        w.Loaded += (_, _) => pb.Focus();
        return w.ShowDialog() == true ? pb.Password : null;
    }

    /// <summary>Returns the index of the chosen option or -1.</summary>
    public static int Choice(string title, string message, params string[] options)
    {
        var w = MakeWindow(title, 420);
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Label(message));
        int result = -1;
        var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < options.Length; i++)
        {
            int k = i;
            var b = new Button { Content = options[i], MinWidth = 90, Margin = new Thickness(6, 0, 0, 0) };
            if (i == 0) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
            b.Click += (_, _) => { result = k; w.DialogResult = true; };
            row.Children.Add(b);
        }
        var cancel = new Button { Content = "Cancel", MinWidth = 70, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        row.Children.Add(cancel);
        sp.Children.Add(row);
        w.Content = sp;
        w.ShowDialog();
        return result;
    }

    // ------------------------------------------------------------ files
    public const string PdfFilter = "PDF (*.pdf)|*.pdf";
    public const string ProjectFilter = "JNN Review Project (*.jnnreview)|*.jnnreview";
    public const string ExcelFilter = "Excel (*.xlsx)|*.xlsx";
    public const string CsvFilter = "CSV (*.csv)|*.csv";

    public static string? OpenFile(string filter, string title = "Open file")
    {
        var d = new OpenFileDialog { Filter = filter + "|All files (*.*)|*.*", Title = title };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string[]? OpenFiles(string filter, string title = "Choose files")
    {
        var d = new OpenFileDialog { Filter = filter, Title = title, Multiselect = true };
        return d.ShowDialog(Owner) == true ? d.FileNames : null;
    }

    public static string? SaveFile(string filter, string defaultName, string title = "Save file")
    {
        var d = new SaveFileDialog { Filter = filter, FileName = defaultName, Title = title, AddExtension = true };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public static string? PickFolder(string title = "Choose folder")
    {
        var d = new OpenFolderDialog { Title = title };
        return d.ShowDialog(Owner) == true ? d.FolderName : null;
    }

    // ------------------------------------------------------------ scale
    public static readonly string[] ScalePresets = { "1:1", "1:2", "1:5", "1:10", "1:20", "1:25", "1:50", "1:75", "1:100", "1:150", "1:200", "1:250", "1:500", "1:1000" };

    /// <summary>Scale dialog: preset ratio or custom "PDF distance = real distance".</summary>
    public static ScaleSetting? ScaleDialog(ScaleSetting current, out bool allPages)
    {
        allPages = true;
        var w = MakeWindow("Set scale", 420);
        var sp = new StackPanel { Margin = new Thickness(16) };
        var rbPreset = new RadioButton { Content = "Standard ratio", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        var cbPreset = new ComboBox { IsEditable = true, ItemsSource = ScalePresets, Text = current.Label.StartsWith("1:") ? current.Label : "1:100" };
        var rbCustom = new RadioButton { Content = "Custom:  PDF distance = Real distance", Margin = new Thickness(0, 12, 0, 4) };
        var grid = new Grid();
        for (int i = 0; i < 5; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 2 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        var tbPaper = new TextBox { Text = current.PaperValue.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 4, 0) };
        var cbPaperU = new ComboBox { ItemsSource = Lists.PaperUnits, SelectedItem = current.PaperUnit, Margin = new Thickness(0, 0, 4, 0) };
        var eq = new TextBlock { Text = " = ", VerticalAlignment = VerticalAlignment.Center };
        var tbReal = new TextBox { Text = current.RealValue.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(4, 0, 4, 0) };
        var cbRealU = new ComboBox { ItemsSource = Lists.RealUnits, SelectedItem = current.RealUnit };
        Grid.SetColumn(tbPaper, 0); Grid.SetColumn(cbPaperU, 1); Grid.SetColumn(eq, 2); Grid.SetColumn(tbReal, 3); Grid.SetColumn(cbRealU, 4);
        grid.Children.Add(tbPaper); grid.Children.Add(cbPaperU); grid.Children.Add(eq); grid.Children.Add(tbReal); grid.Children.Add(cbRealU);
        var cbDisplay = new ComboBox { ItemsSource = Lists.RealUnits, SelectedItem = current.DisplayUnit };
        var chkAll = new CheckBox { Content = "Apply to all pages", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
        sp.Children.Add(rbPreset); sp.Children.Add(cbPreset); sp.Children.Add(rbCustom); sp.Children.Add(grid);
        sp.Children.Add(Label("Display unit:")); sp.Children.Add(cbDisplay); sp.Children.Add(chkAll);
        ScaleSetting? result = null;
        sp.Children.Add(Buttons(w, () =>
        {
            try
            {
                if (rbPreset.IsChecked == true)
                {
                    if (!MeasurementService.TryParseRatio(cbPreset.Text, out var s)) { Error("Invalid ratio. Example: 1:100"); return false; }
                    result = s;
                }
                else
                {
                    double pv = double.Parse(tbPaper.Text.Replace(',', '.'), CultureInfo.InvariantCulture);
                    double rv = double.Parse(tbReal.Text.Replace(',', '.'), CultureInfo.InvariantCulture);
                    if (pv <= 0 || rv <= 0) throw new FormatException();
                    result = new ScaleSetting { PaperValue = pv, PaperUnit = (string)cbPaperU.SelectedItem, RealValue = rv, RealUnit = (string)cbRealU.SelectedItem };
                }
                result.DisplayUnit = (string?)cbDisplay.SelectedItem ?? "m";
                MeasurementService.RealPerPoint(result); // validates
                return true;
            }
            catch { Error("Invalid scale value (must be a number > 0)."); return false; }
        }));
        w.Content = sp;
        bool ok = w.ShowDialog() == true;
        allPages = chkAll.IsChecked == true;
        return ok ? result : null;
    }

    public static (double Length, string Unit, bool AllPages)? CalibrateDialog(double lengthPt)
    {
        var w = MakeWindow("Calibrate", 380);
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Label($"The drawn line is {lengthPt:0.##} pt ({lengthPt * 25.4 / 72:0.##} mm on paper).\nEnter the real length:"));
        var row = new DockPanel();
        var cb = new ComboBox { ItemsSource = Lists.RealUnits, SelectedItem = "m", Width = 70, Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(cb, Dock.Right);
        var tb = new TextBox { Text = "1" };
        row.Children.Add(cb); row.Children.Add(tb);
        sp.Children.Add(row);
        var chk = new CheckBox { Content = "Apply to all pages", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        sp.Children.Add(chk);
        double val = 0;
        sp.Children.Add(Buttons(w, () =>
        {
            if (!double.TryParse(tb.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out val) || val <= 0)
            { Error("Length must be a number > 0."); return false; }
            return true;
        }));
        w.Content = sp;
        w.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return w.ShowDialog() == true ? (val, (string)cb.SelectedItem, chk.IsChecked == true) : null;
    }

    public static ExportService.ReportInfo? ReportDialog(ExportService.ReportInfo def)
    {
        var w = MakeWindow("Report", 480);
        var sp = new StackPanel { Margin = new Thickness(16) };
        TextBox Field(string label, string v, bool multi = false)
        {
            sp.Children.Add(Label(label));
            var t = new TextBox { Text = v, AcceptsReturn = multi, TextWrapping = TextWrapping.Wrap, Height = multi ? 90 : double.NaN };
            sp.Children.Add(t); return t;
        }
        var p = Field("Project", def.Project); var d = Field("Drawing", def.Drawing); var r = Field("Revision", def.Revision);
        var rv = Field("Reviewer", def.Reviewer); var s = Field("Summary", def.Summary, true);
        sp.Children.Add(Buttons(w, null, "Export report"));
        w.Content = sp;
        return w.ShowDialog() == true ? new ExportService.ReportInfo(p.Text, d.Text, r.Text, rv.Text, s.Text) : null;
    }
}

// ============================================================================
//  Batch processing window: Merge / Stamp / Export PNG / Zone check /
//  Compare folders / Rename by sheet number.
// ============================================================================
public sealed class BatchWindow : Window
{
    private readonly ObservableCollection<string> _files = new();
    private readonly ComboBox _op = new();
    private readonly TextBox _log = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas") };
    private readonly ProgressBar _progress = new() { Height = 6, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _param = new();
    private readonly TextBlock _paramLabel = new() { Margin = new Thickness(0, 8, 0, 2) };
    private readonly Func<IReadOnlyList<Zone>> _zones;
    private CancellationTokenSource? _cts;

    private static readonly string[] Ops =
    {
        "Merge PDF", "Add stamp", "Export PNG (page 1 of each file)", "Export PNG (all pages)",
        "Zone check (current zones)", "Compare: folder A vs folder B (same file names)", "Rename by drawing number (zones Sheet/Title/Rev)"
    };

    public BatchWindow(Func<IReadOnlyList<Zone>> zones)
    {
        _zones = zones;
        Title = "JNN PDF – Batch Processing";
        Width = 760; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        Owner = Application.Current.MainWindow;

        var root = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        var fileButtons = new WrapPanel();
        var bAdd = new Button { Content = "+ Add files…", Margin = new Thickness(0, 0, 6, 0) };
        var bFolder = new Button { Content = "+ Add folder…", Margin = new Thickness(0, 0, 6, 0) };
        var bClear = new Button { Content = "Clear list" };
        bAdd.Click += (_, _) => { foreach (var f in Dialogs.OpenFiles(Dialogs.PdfFilter) ?? Array.Empty<string>()) if (!_files.Contains(f)) _files.Add(f); };
        bFolder.Click += (_, _) =>
        {
            var d = Dialogs.PickFolder();
            if (d == null) return;
            foreach (var f in Directory.EnumerateFiles(d, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(x => x)) if (!_files.Contains(f)) _files.Add(f);
        };
        bClear.Click += (_, _) => _files.Clear();
        fileButtons.Children.Add(bAdd); fileButtons.Children.Add(bFolder); fileButtons.Children.Add(bClear);
        top.Children.Add(fileButtons);
        var list = new ListBox { ItemsSource = _files, Height = 170, Margin = new Thickness(0, 8, 0, 0) };
        top.Children.Add(list);
        top.Children.Add(new TextBlock { Text = "Operation:", Margin = new Thickness(0, 10, 0, 2) });
        _op.ItemsSource = Ops; _op.SelectedIndex = 0;
        _op.SelectionChanged += (_, _) => UpdateParam();
        top.Children.Add(_op);
        top.Children.Add(_paramLabel);
        top.Children.Add(_param);
        var run = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var bRun = new Button { Content = "▶ Run", MinWidth = 100, Margin = new Thickness(0, 0, 6, 0) };
        bRun.SetResourceReference(StyleProperty, "AccentButton");
        var bStop = new Button { Content = "■ Stop", MinWidth = 80 };
        bRun.Click += async (_, _) => await RunAsync();
        bStop.Click += (_, _) => _cts?.Cancel();
        run.Children.Add(bRun); run.Children.Add(bStop);
        top.Children.Add(run);
        top.Children.Add(_progress);
        root.Children.Add(top);
        _log.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_log);
        Content = root;
        UpdateParam();
    }

    private void UpdateParam()
    {
        (_paramLabel.Text, _param.Text) = _op.SelectedIndex switch
        {
            1 => ("Stamp text | Revision:", "APPROVED | A"),
            2 or 3 => ("DPI:", "150"),
            5 => ("Folder B (new revision):", ""),
            6 => ("Name pattern (use {Sheet} {Title} {Rev} {Name}):", "{Sheet}_{Rev}_{Title}"),
            _ => ("Parameter:", "")
        };
        _param.IsEnabled = _op.SelectedIndex is 1 or 2 or 3 or 5 or 6;
    }

    private void Log(string s) => Dispatcher.Invoke(() => { _log.AppendText(s + Environment.NewLine); _log.ScrollToEnd(); });

    private async Task RunAsync()
    {
        if (_files.Count == 0) { Dialogs.Info("No files added."); return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var files = _files.ToList();
        int op = _op.SelectedIndex;
        string param = _param.Text;
        _progress.Maximum = files.Count; _progress.Value = 0;
        Log($"=== {Ops[op]} – {files.Count} file ===");
        try
        {
            switch (op)
            {
                case 0:
                    {
                        var outPath = Dialogs.SaveFile(Dialogs.PdfFilter, "Merged.pdf");
                        if (outPath == null) return;
                        await Task.Run(() => ExportService.MergePdfs(files, outPath, ct, new Progress<string>(f => { Log("+ " + f); Dispatcher.Invoke(() => _progress.Value++); })), ct);
                        Log("✔ Merged: " + outPath);
                        break;
                    }
                case 1:
                    {
                        var outDir = Dialogs.PickFolder("Output folder for stamped files");
                        if (outDir == null) return;
                        var parts = param.Split('|');
                        string stamp = parts[0].Trim(), rev = parts.Length > 1 ? parts[1].Trim() : "";
                        foreach (var f in files)
                        {
                            ct.ThrowIfCancellationRequested();
                            string o = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "_stamped.pdf");
                            await Task.Run(() => ExportService.StampPdf(f, o, stamp, Environment.UserName, rev), ct);
                            Log("✔ " + o); _progress.Value++;
                        }
                        break;
                    }
                case 2:
                case 3:
                    {
                        var outDir = Dialogs.PickFolder("Output folder for PNG");
                        if (outDir == null) return;
                        double dpi = double.TryParse(param, out var d) ? Math.Clamp(d, 36, 600) : 150;
                        foreach (var f in files)
                        {
                            ct.ThrowIfCancellationRequested();
                            using var doc = await Task.Run(() => PdfDocument.Open(f), ct);
                            int n = op == 2 ? 1 : doc.PageCount;
                            for (int p = 0; p < n; p++)
                            {
                                ct.ThrowIfCancellationRequested();
                                var bmp = await Task.Run(() => doc.Render(p, dpi / 72.0), ct);
                                string o = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(f)}_p{p + 1:000}.png");
                                ExportService.SavePng(bmp, o);
                                Log("✔ " + o);
                            }
                            _progress.Value++;
                        }
                        break;
                    }
                case 4:
                    {
                        var zones = _zones();
                        if (zones.Count == 0) { Dialogs.Info("No check zones yet. Create zones with the 'Check zone' tool first."); return; }
                        var all = new List<ZoneResult>();
                        foreach (var f in files)
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                using var doc = await Task.Run(() => PdfDocument.Open(f), ct);
                                var res = await Task.Run(() => ZoneService.Run(zones, doc, null, Path.GetFileName(f), ct), ct);
                                all.AddRange(res);
                                Log($"✔ {Path.GetFileName(f)}: {res.Count(r => r.Passed)} PASS / {res.Count(r => !r.Passed)} FAIL");
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException) { Log($"✖ {Path.GetFileName(f)}: {ex.Message}"); }
                            _progress.Value++;
                        }
                        var outPath = Dialogs.SaveFile(Dialogs.ExcelFilter, "ZoneCheck_Batch.xlsx");
                        if (outPath != null) { ExportService.ExportExcel(outPath, ExportService.ZoneResultsTable(all)); Log("✔ Excel: " + outPath); }
                        break;
                    }
                case 5:
                    {
                        if (!Directory.Exists(param)) { Dialogs.Error("Enter a valid Folder B path."); return; }
                        var outDir = Dialogs.PickFolder("Output folder for compare PDFs");
                        if (outDir == null) return;
                        var summary = new List<object?[]>();
                        foreach (var f in files)
                        {
                            ct.ThrowIfCancellationRequested();
                            string fb = Path.Combine(param, Path.GetFileName(f));
                            if (!File.Exists(fb)) { Log($"– {Path.GetFileName(f)}: not found in folder B"); _progress.Value++; continue; }
                            try
                            {
                                using var a = await Task.Run(() => PdfDocument.Open(f), ct);
                                using var b = await Task.Run(() => PdfDocument.Open(fb), ct);
                                var changes = new List<ChangeRegion>();
                                int n = Math.Min(a.PageCount, b.PageCount);
                                for (int p = 0; p < n; p++) changes.AddRange(await Task.Run(() => CompareService.ComparePage(a, p, b, p, ct), ct));
                                string o = Path.Combine(outDir, Path.GetFileNameWithoutExtension(f) + "_compare.pdf");
                                await Task.Run(() => ExportService.ExportFlattenedPdf(b, o, Array.Empty<Markup>(), _ => ScaleSetting.Ratio(100), changes), ct);
                                summary.Add(new object?[] { Path.GetFileName(f), n, changes.Count, a.PageCount != b.PageCount ? $"Page count differs: {a.PageCount} vs {b.PageCount}" : "" });
                                Log($"✔ {Path.GetFileName(f)}: {changes.Count} change(s) → {o}");
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException) { Log($"✖ {Path.GetFileName(f)}: {ex.Message}"); }
                            _progress.Value++;
                        }
                        string xl = Path.Combine(outDir, "Compare_Summary.xlsx");
                        ExportService.ExportExcel(xl, new TableData("Summary", new[] { "File", "Pages", "Changes", "Note" }, summary));
                        Log("✔ Summary: " + xl);
                        break;
                    }
                case 6:
                    {
                        var zones = _zones();
                        if (!Dialogs.Confirm("Rename the files on disk using this pattern?\n" + param)) return;
                        foreach (var f in files.ToList())
                        {
                            ct.ThrowIfCancellationRequested();
                            try
                            {
                                DrawingSheet ds;
                                using (var doc = await Task.Run(() => PdfDocument.Open(f), ct))
                                    ds = ZoneService.DetectSheet(doc, 0, zones);
                                string name = param.Replace("{Sheet}", ds.Sheet).Replace("{Title}", ds.Title).Replace("{Rev}", ds.Revision).Replace("{Name}", Path.GetFileNameWithoutExtension(f));
                                name = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim('_', ' ');
                                if (string.IsNullOrWhiteSpace(name)) { Log($"– {Path.GetFileName(f)}: could not read a name"); continue; }
                                string target = Path.Combine(Path.GetDirectoryName(f)!, name + ".pdf");
                                if (File.Exists(target)) { Log($"– {Path.GetFileName(f)}: {name}.pdf already exists"); continue; }
                                File.Move(f, target);
                                _files[_files.IndexOf(f)] = target;
                                Log($"✔ {Path.GetFileName(f)} → {name}.pdf");
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException) { Log($"✖ {Path.GetFileName(f)}: {ex.Message}"); }
                            _progress.Value++;
                        }
                        break;
                    }
            }
            Log("=== Done ===");
        }
        catch (OperationCanceledException) { Log("■ Stopped."); }
        catch (Exception ex) { Logger.Error("Batch", ex); Log("✖ Error: " + ex.Message); }
    }
}
