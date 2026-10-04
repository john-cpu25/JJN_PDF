// ============================================================================
//  MainWindow.xaml.cs  –  VIEW CODE-BEHIND
//  Thin glue between the XAML UI, the PdfCanvas (view/markup engine) and the
//  MainViewModel. Implements IViewer so the ViewModel can drive navigation
//  without referencing WPF controls. Contains only UI plumbing:
//  events, keyboard shortcuts, drag & drop, grid double-clicks, crash recovery.
// ============================================================================
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace JnnPdf;

public partial class MainWindow : Window, IViewer
{
    private readonly MainViewModel _vm;
    private bool _syncing;              // guards against feedback loops (VM <-> canvas <-> thumbnails)
    private Point _thumbDragStart;
    private ThumbnailItem? _thumbDragItem;

    static MainWindow()
    {
        // WPF DataGrid quirk: when a grid with a "*" column is first measured very narrow (e.g. before the
        // window is maximized) the fixed columns get squeezed to MinWidth and never grow back.
        // Re-applying the declared widths after a resize restores them.
        EventManager.RegisterClassHandler(typeof(DataGrid), SizeChangedEvent, new SizeChangedEventHandler((s, e) =>
        {
            if (s is DataGrid g && e.WidthChanged) FixColumns(g);
        }));
        EventManager.RegisterClassHandler(typeof(DataGrid), LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is DataGrid g) FixColumns(g);
        }));
    }

    private static void FixColumns(DataGrid g) => g.Dispatcher.BeginInvoke(() =>
    {
        if (g.ActualWidth < 200 || !g.Columns.Any(c => c.Width.IsAbsolute && c.ActualWidth < c.Width.Value - 1)) return;
        foreach (var c in g.Columns) { var w = c.Width; c.Width = 0; c.Width = w; }
    }, System.Windows.Threading.DispatcherPriority.Background);

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.Viewer = this;

        WireCanvas();
        _vm.PropertyChanged += Vm_PropertyChanged;
        _vm.Changes.CollectionChanged += (_, _) => Canvas.InvalidateVisual();
        _vm.Markups.CollectionChanged += (_, _) => FixColumns(MarkupGrid);
        _vm.SearchHits.CollectionChanged += (_, _) => Canvas.InvalidateVisual();
        _vm.Zones.CollectionChanged += (_, _) => Canvas.InvalidateVisual();

        PreviewKeyDown += Window_PreviewKeyDown;
        KeyDown += Window_KeyDown;
        PreviewKeyUp += Window_PreviewKeyUp;
        Drop += Window_Drop;
        DragOver += Window_DragOver;
        Closing += Window_Closing;
        Loaded += Window_Loaded;
    }

    // =================================================================== canvas wiring
    private void WireCanvas()
    {
        Canvas.Markups = _vm.Markups;
        Canvas.ScaleProvider = _vm.ScaleOf;
        Canvas.ExtraWords = _vm.OcrWords;
        Canvas.Zones = _vm.Zones;
        Canvas.SearchHits = _vm.SearchHits;
        Canvas.Changes = _vm.Changes;
        Canvas.Tool = _vm.CurrentTool;
        Canvas.CurrentColor = _vm.CurrentColor;
        Canvas.CurrentLineWidth = _vm.CurrentLineWidth;
        Canvas.CurrentFontSize = _vm.CurrentFontSize;
        Canvas.CurrentTag = _vm.CurrentTag;
        Canvas.CurrentStamp = _vm.CurrentStamp;
        UpdateCanvasBackground();

        Canvas.MarkupCreated += m => SafeUi(() => _vm.AddMarkup(m));
        Canvas.SelectionChanged += m => { if (_syncing) return; _syncing = true; try { _vm.SelectedMarkup = m; } finally { _syncing = false; } };
        Canvas.MarkupEdited += m => { m.ModifiedDate = DateTime.Now; _vm.CommitNow(); };
        Canvas.EditTextRequested += m => SafeUi(() =>
        {
            var t = Dialogs.Prompt("Edit text", "Text:", m.Text, true);
            if (t != null && t != m.Text) { m.Text = t; m.ModifiedDate = DateTime.Now; _vm.CommitNow(); }
        });
        Canvas.HyperlinkActivated += m => _vm.FollowLink(m);
        Canvas.RegionSelected += OnRegionSelected;
        Canvas.CalibrationLine += len => SafeUi(() => _vm.OnCalibration(len));
        Canvas.PointerMoved += OnPointerMoved;
        Canvas.LiveMeasurement += s => _vm.MeasureText = s;
        Canvas.DeleteRequested += () => { if (_vm.DeleteMarkupCommand.CanExecute(null)) _vm.DeleteMarkupCommand.Execute(null); };
        Canvas.ViewChanged += OnViewChanged;
    }

    /// <summary>Runs a UI action with error reporting so a failing handler never crashes the app.</summary>
    private static void SafeUi(Action a)
    {
        try { a(); }
        catch (Exception ex) { Logger.Error("UI action", ex); Dialogs.Error(ex.Message); }
    }

    private void UpdateCanvasBackground()
    {
        if (TryFindResource("CanvasBrush") is Brush b) Canvas.CanvasBackground = b;
        Canvas.InvalidateVisual();
    }

    private void OnRegionSelected(RegionEventArgs e) => SafeUi(() =>
    {
        switch (e.Tool)
        {
            case ToolKind.RegionRead:
                _vm.ReadRegion(e.Page, e.Rect);
                RightTabs.SelectedItem = RegionTab;
                break;
            case ToolKind.Zone:
                _vm.AddZone(e.Page, e.Rect);
                break;
            case ToolKind.Crop:
                _vm.Crop(e.Page, e.Rect);
                break;
            case ToolKind.TextSelect:
                _vm.SelectText(e.Page, e.Rect);
                RightTabs.SelectedItem = RegionTab;
                break;
        }
        Canvas.InvalidateVisual();
    });

    private void OnPointerMoved(PdfPoint? p)
    {
        if (p == null) { _vm.CoordText = ""; return; }
        var pt = p.Value;
        string real = "";
        try
        {
            var s = _vm.ScaleOf(Canvas.PageIndex);
            double k = MeasurementService.RealPerPoint(s);
            real = $"  |  {pt.X * k:0.00}, {pt.Y * k:0.00} {s.DisplayUnit}";
        }
        catch { /* invalid scale – show points only */ }
        _vm.CoordText = $"X {pt.X:0.0}  Y {pt.Y:0.0} pt{real}";
    }

    private void OnViewChanged()
    {
        var doc = Canvas.Document;
        if (doc == null) return;
        int page = Canvas.PageIndex;
        _vm.OnPageChanged(page, doc.PageCount, Canvas.Zoom);
        _syncing = true;
        try
        {
            if (!PageBox.IsKeyboardFocusWithin) PageBox.Text = (page + 1).ToString();
            PageCountText.Text = "/ " + doc.PageCount;
            if (!ZoomBox.IsKeyboardFocusWithin) ZoomBox.Text = $"{Canvas.Zoom * 72 / 96 * 100:0}%";
            if (page < _vm.Thumbnails.Count && ThumbList.SelectedIndex != page)
            {
                ThumbList.SelectedIndex = page;
                ThumbList.ScrollIntoView(ThumbList.SelectedItem);
            }
        }
        finally { _syncing = false; }
    }

    // =================================================================== VM -> canvas
    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentTool): Canvas.Tool = _vm.CurrentTool; break;
            case nameof(MainViewModel.CurrentColor): Canvas.CurrentColor = _vm.CurrentColor; break;
            case nameof(MainViewModel.CurrentLineWidth): Canvas.CurrentLineWidth = _vm.CurrentLineWidth; break;
            case nameof(MainViewModel.CurrentFontSize): Canvas.CurrentFontSize = _vm.CurrentFontSize; break;
            case nameof(MainViewModel.CurrentTag): Canvas.CurrentTag = _vm.CurrentTag; break;
            case nameof(MainViewModel.CurrentStamp): Canvas.CurrentStamp = _vm.CurrentStamp; break;
            case nameof(MainViewModel.OverlayMode): Canvas.Overlay = _vm.OverlayMode; break;
            case nameof(MainViewModel.OverlayOpacity): Canvas.OverlayOpacity = _vm.OverlayOpacity / 100.0; break;
            case nameof(MainViewModel.OverlayOffsetX): Canvas.OverlayOffsetX = _vm.OverlayOffsetX; break;
            case nameof(MainViewModel.OverlayOffsetY): Canvas.OverlayOffsetY = _vm.OverlayOffsetY; break;
            case nameof(MainViewModel.OverlayRotation): Canvas.OverlayRotation = _vm.OverlayRotation; break;
            case nameof(MainViewModel.OverlayScale): Canvas.OverlayScale = _vm.OverlayScale; break;
            case nameof(MainViewModel.CurrentChange): Canvas.CurrentChange = _vm.CurrentChange; Canvas.InvalidateVisual(); break;
            case nameof(MainViewModel.SelectedMarkup):
                if (_syncing) break;
                _syncing = true;
                try { Canvas.SelectedMarkup = _vm.SelectedMarkup != null && _vm.SelectedMarkup.Page == Canvas.PageIndex ? _vm.SelectedMarkup : null; }
                finally { _syncing = false; }
                break;
            case nameof(MainViewModel.IsDark):
                Dispatcher.BeginInvoke(UpdateCanvasBackground, System.Windows.Threading.DispatcherPriority.Background);
                break;
        }
    }

    // =================================================================== IViewer
    public int CurrentPage => Canvas.PageIndex;

    public void ShowDocument(PdfDocument? doc)
    {
        Canvas.Document = doc;
        Canvas.TextSelection.Clear();
        if (doc != null)
        {
            Canvas.GoToPage(0);
            PageCountText.Text = "/ " + doc.PageCount;
            Canvas.Focus();
        }
        else
        {
            PageBox.Text = "";
            PageCountText.Text = "/ –";
            ZoomBox.Text = "";
        }
    }

    public void SetCompareDocument(PdfDocument? doc)
    {
        if (doc != null) Canvas.PageB = Math.Min(Canvas.PageIndex, doc.PageCount - 1);
        Canvas.DocumentB = doc;
        Canvas.Overlay = _vm.OverlayMode;
    }

    public void GoToPage(int page)
    {
        if (Canvas.Document == null) return;
        if (page < 0 || page >= Canvas.Document.PageCount) { Dialogs.Error($"Invalid page: {page + 1} (1 – {Canvas.Document.PageCount})"); return; }
        Canvas.GoToPage(page);
    }

    public void ZoomToRect(int page, PdfRect rect)
    {
        if (Canvas.Document == null) return;
        page = Math.Clamp(page, 0, Canvas.Document.PageCount - 1);
        if (page != Canvas.PageIndex) Canvas.GoToPage(page);
        // GoToPage may defer FitPage until layout; zoom after it at lower priority
        Dispatcher.BeginInvoke(() =>
        {
            Canvas.ZoomToRect(rect);
            if (_vm.SelectedMarkup != null && _vm.SelectedMarkup.Page == page) Canvas.SelectedMarkup = _vm.SelectedMarkup;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void ViewCommand(string cmd)
    {
        if (Canvas.Document == null) return;
        switch (cmd)
        {
            case "zoomin": Canvas.ZoomBy(1.25); break;
            case "zoomout": Canvas.ZoomBy(0.8); break;
            case "fitpage": Canvas.FitPage(); break;
            case "fitwidth": Canvas.FitWidth(); break;
            case "actual": Canvas.ActualSize(); break;
            case "rotl": Canvas.Rotate(-90); break;
            case "rotr": Canvas.Rotate(90); break;
            case "prev": Canvas.GoToPage(Canvas.PageIndex - 1); break;
            case "next": Canvas.GoToPage(Canvas.PageIndex + 1); break;
            case "first": Canvas.GoToPage(0); break;
            case "last": Canvas.GoToPage(Canvas.Document.PageCount - 1); break;
            case "goto": PromptGoTo(); break;
        }
    }

    public void Refresh() => Canvas.InvalidateVisual();

    private void PromptGoTo()
    {
        var s = Dialogs.Prompt("Go to page", $"Page (1 – {Canvas.Document?.PageCount}):", (Canvas.PageIndex + 1).ToString());
        if (s != null && int.TryParse(s.Trim(), out int p)) GoToPage(p - 1);
    }

    // =================================================================== window lifecycle
    private bool _snapshotMode;

    /// <summary>
    /// Dev/QA helper (--snapshot): opens PDF 1 (+ PDF 2), adds a few demo markups, renders the
    /// real window to PNG via RenderTargetBitmap and exits. Works without an interactive desktop.
    /// </summary>
    public async Task SnapshotAsync(string png, string? pdf, string? pdf2)
    {
        _snapshotMode = true;
        try
        {
            await Task.Delay(500);
            if (pdf != null && File.Exists(pdf) && await _vm.OpenPdfAsync(pdf))
            {
                await Task.Delay(800);
                var s = Canvas.Document!.GetPageSize(0);
                double w = s.Width, h = s.Height;
                void Add(MarkupType t, string color, params (double X, double Y)[] pts)
                    => _vm.AddMarkup(new Markup { Type = t, Page = 0, Color = color, LineWidth = 3, Points = pts.Select(p => new PdfPoint(p.X * w, p.Y * h)).ToList() });
                Add(MarkupType.Cloud, "#E53935", (0.30, 0.30), (0.45, 0.45));
                Add(MarkupType.Length, "#1E88E5", (0.10, 0.80), (0.40, 0.80));
                Add(MarkupType.Area, "#43A047", (0.55, 0.20), (0.70, 0.20), (0.72, 0.40), (0.55, 0.42));
                Add(MarkupType.Count, "#FB8C00", (0.20, 0.20), (0.22, 0.20), (0.24, 0.20), (0.26, 0.20));
                Add(MarkupType.Rectangle, "#8E24AA", (0.78, 0.60), (0.95, 0.75));
                if (pdf2 != null && File.Exists(pdf2)) { await _vm.OpenCompareAsync(pdf2); RightTabs.SelectedIndex = 3; }
                Canvas.FitPage();
            }
            await Task.Delay(3500); // let async page / tile rendering finish
            UpdateLayout();
            var root = (FrameworkElement)Content;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(root.ActualWidth * dpi), (int)(root.ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((Brush)FindResource("WindowBrush"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = File.Create(png)) enc.Save(fs);
            Logger.Info("Snapshot saved " + png);
        }
        catch (Exception ex) { Logger.Error("Snapshot", ex); }
        _vm.IsDirty = false;
        Application.Current.Shutdown();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_snapshotMode) return;
        try
        {
            // crash recovery: a session marker left behind means the last run did not exit cleanly
            var crashed = ProjectService.GetCrashedSession();
            ProjectService.MarkSession(null, null);
            if (crashed != null && File.Exists(crashed.Value.Backup) &&
                Dialogs.Confirm($"JNN PDF did not close properly last time.\nRecover the latest autosave?\n\n{crashed.Value.Backup}"))
            {
                await _vm.RecoverAsync(crashed.Value.Backup, crashed.Value.Project);
                return;
            }
            // file passed on the command line (e.g. "Open with")
            var args = Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith("--")).ToList();
            if (args.Count > 0 && File.Exists(args[0])) await _vm.OpenAnyAsync(args[0]);
        }
        catch (Exception ex) { Logger.Error("Startup", ex); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            if (!_snapshotMode && !_vm.ConfirmClose()) { e.Cancel = true; return; }
            _vm.CloseDocument(force: true);
        }
        catch (Exception ex) { Logger.Error("Closing", ex); }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Handled || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var f = files[0];
        if (!(f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(ProjectService.Extension, StringComparison.OrdinalIgnoreCase)))
        {
            Dialogs.Error("Only PDF files or projects can be dropped: " + ProjectService.Extension);
            return;
        }
        // Ctrl + drop a PDF while a document is open = open as PDF 2 (compare / overlay)
        if (_vm.HasDocument && f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && (e.KeyStates & DragDropKeyStates.ControlKey) != 0)
            await _vm.OpenCompareAsync(f);
        else
            await _vm.OpenAnyAsync(f);
    }

    // =================================================================== keyboard
    private static bool IsTextInputFocused()
        => Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox
           || (Keyboard.FocusedElement is DependencyObject d && FindAncestor<ComboBox>(d) != null)
           || (Keyboard.FocusedElement is DataGridCell c && c.IsEditing);

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            var r = FindVisualChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>Preview: keys that must win over focused buttons (Space) and global shortcuts.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        // Ctrl+F search, Ctrl+G go to page
        if (mods == ModifierKeys.Control && e.Key == Key.F) { FocusSearch(); e.Handled = true; return; }
        if (mods == ModifierKeys.Control && e.Key == Key.G) { ViewCommand("goto"); e.Handled = true; return; }
        // F3 / Shift+F3 next / previous change
        if (e.Key == Key.F3)
        {
            var cmd = mods == ModifierKeys.Shift ? _vm.PrevChangeCommand : _vm.NextChangeCommand;
            if (cmd.CanExecute(null)) cmd.Execute(null);
            e.Handled = true; return;
        }
        // Alt + arrows: nudge PDF 2 overlay (Shift = 10x)
        if (e.Key == Key.System && (mods & ModifierKeys.Alt) != 0 && _vm.HasCompare)
        {
            double step = (mods & ModifierKeys.Shift) != 0 ? 10 : 1;
            switch (e.SystemKey)
            {
                case Key.Left: _vm.OverlayOffsetX -= step; e.Handled = true; break;
                case Key.Right: _vm.OverlayOffsetX += step; e.Handled = true; break;
                case Key.Up: _vm.OverlayOffsetY -= step; e.Handled = true; break;
                case Key.Down: _vm.OverlayOffsetY += step; e.Handled = true; break;
            }
            if (e.Handled) _vm.StatusMessage = $"Overlay offset: {_vm.OverlayOffsetX:0}, {_vm.OverlayOffsetY:0} pt";
            return;
        }
        // Space held = temporary pan
        if (e.Key == Key.Space && mods == ModifierKeys.None && !IsTextInputFocused() && _vm.HasDocument)
        {
            if (!e.IsRepeat) { Canvas.TemporaryPan = true; Canvas.Cursor = Cursors.SizeAll; }
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Canvas.TemporaryPan)
        {
            Canvas.TemporaryPan = false;
            Canvas.Tool = _vm.CurrentTool; // restores the tool cursor
            e.Handled = true;
        }
    }

    /// <summary>Bubbling: single-letter tool shortcuts, Esc, Delete (canvas handles its own keys first).</summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || IsTextInputFocused() || Keyboard.Modifiers != ModifierKeys.None) return;
        ToolKind? tool = e.Key switch
        {
            Key.V => ToolKind.Select,
            Key.H => ToolKind.Pan,
            Key.Z => ToolKind.ZoomBox,
            Key.M => ToolKind.Length,
            Key.T => ToolKind.TextBox,
            Key.C => ToolKind.Cloud,
            Key.R => ToolKind.Rectangle,
            Key.L => ToolKind.Line,
            Key.A => ToolKind.Area,
            Key.N => ToolKind.Count,
            Key.D => ToolKind.RegionRead,
            _ => null
        };
        if (tool != null) { _vm.CurrentTool = tool.Value; e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Escape:
                Canvas.CancelDrawing();
                Canvas.TextSelection.Clear();
                _vm.CurrentTool = ToolKind.Select;
                Canvas.InvalidateVisual();
                e.Handled = true; break;
            case Key.Enter when Keyboard.FocusedElement is not ButtonBase:
                Canvas.FinishDrawing(); e.Handled = true; break;
            case Key.Delete:
                // in the markups list: delete all selected rows; elsewhere the selected markup
                object? param = MarkupGrid.IsKeyboardFocusWithin ? MarkupGrid.SelectedItems : null;
                if (_vm.DeleteMarkupCommand.CanExecute(param)) _vm.DeleteMarkupCommand.Execute(param);
                e.Handled = true; break;
            case Key.PageDown: ViewCommand("next"); e.Handled = true; break;
            case Key.PageUp: ViewCommand("prev"); e.Handled = true; break;
            case Key.Home when !ThumbList.IsKeyboardFocusWithin: ViewCommand("first"); e.Handled = true; break;
            case Key.End when !ThumbList.IsKeyboardFocusWithin: ViewCommand("last"); e.Handled = true; break;
        }
    }

    private void FocusSearch()
    {
        LeftTabs.SelectedItem = SearchTab;
        Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    // =================================================================== menu handlers
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void FocusSearch_Click(object sender, RoutedEventArgs e) => FocusSearch();

    private void ToggleZones_Click(object sender, RoutedEventArgs e)
    {
        Canvas.ShowZones = (sender as MenuItem)?.IsChecked ?? true;
        Canvas.InvalidateVisual();
    }

    private void ToggleAnnots_Click(object sender, RoutedEventArgs e)
    {
        bool on = (sender as MenuItem)?.IsChecked ?? true;
        if (_vm.Doc != null) _vm.Doc.RenderAnnotations = on;
        if (_vm.DocB != null) _vm.DocB.RenderAnnotations = on;
        Canvas.Rerender();
        _vm.StatusMessage = on ? "Showing existing PDF annotations" : "Hiding existing PDF annotations";
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => Dialogs.Info(
        "KEYBOARD SHORTCUTS\n\n" +
        "Ctrl+O  Open PDF        Ctrl+S  Save project     Ctrl+Shift+S  Save As\n" +
        "Ctrl+Z  Undo            Ctrl+Y  Redo             Ctrl+P  In\n" +
        "Ctrl+F  Find text       Ctrl+G  Go to page       Ctrl+D  Duplicate markup\n" +
        "Ctrl + / Ctrl -  Zoom   Ctrl+0  Fit page         Mouse wheel  Zoom\n" +
        "PgUp / PgDn  Previous / next page     Home / End  First / last page\n\n" +
        "Space (hold)  Temporary pan     Middle mouse  Pan\n" +
        "V Select   H Pan   Z Zoom box   T Text   R Rectangle   C Cloud   L Line\n" +
        "M Length   A Area   N Count   D Read region data\n" +
        "Shift while drawing  Snap to 0/45/90°\n" +
        "Enter / Double-click  Finish polyline / polygon / area / count\n" +
        "Backspace  Remove last point while drawing     Esc  Cancel / back to Select\n" +
        "Delete  Delete selected markup\n\n" +
        "F3 / Shift+F3  Next / previous change (Compare)\n" +
        "Alt + arrows  Nudge PDF 2 overlay (Shift = faster)\n" +
        "Ctrl + drop a PDF  Open it as PDF 2", "JNN PDF – Keyboard shortcuts");

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Logs}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }

    private void About_Click(object sender, RoutedEventArgs e) => Dialogs.Info(
        "JNN PDF – PDF Review & Construction Drawing Tool\n" +
        $"Version {typeof(MainWindow).Assembly.GetName().Version}\n\n" +
        "C# / WPF / .NET 8 – PDFium rendering, PDFsharp, ClosedXML, SQLite.\n" +
        "Markup • Measurement • Takeoff • Compare/Overlay PDF 1 vs PDF 2 •\n" +
        "Region data • Check zones • Crop • QA/QC • Drawing Register • Report.\n\n" +
        $"Local data: {AppPaths.Root}", "About JNN PDF");

    // =================================================================== toolbar handlers
    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (int.TryParse(PageBox.Text.Trim(), out int p)) GoToPage(p - 1);
        else Dialogs.Error("Invalid page number.");
        Canvas.Focus();
        e.Handled = true;
    }

    private void ZoomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyZoomText(ZoomBox.Text);
        Canvas.Focus();
        e.Handled = true;
    }

    private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ZoomBox.SelectedItem is not ComboBoxItem item) return;
        ApplyZoomText(item.Content?.ToString() ?? "");
        // keep the editable text free for the live zoom value
        Dispatcher.BeginInvoke(() => { _syncing = true; ZoomBox.SelectedIndex = -1; ZoomBox.Text = $"{Canvas.Zoom * 72 / 96 * 100:0}%"; _syncing = false; },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ApplyZoomText(string text)
    {
        if (Canvas.Document == null) return;
        if (double.TryParse(text.Replace("%", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) && pct >= 1 && pct <= 6400)
            Canvas.SetZoom(pct / 100.0 * 96.0 / 72.0, new Point(Canvas.ActualWidth / 2, Canvas.ActualHeight / 2));
        else Dialogs.Error("Invalid zoom (1 – 6400%).");
    }

    private void Palette_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string color) return;
        _vm.CurrentColor = color;
        // Bluebeam-like: picking a colour also recolours the selected markup
        if (_vm.SelectedMarkup != null && _vm.SelectedMarkup.Color != color)
        {
            _vm.SelectedMarkup.Color = color;
            _vm.SelectedMarkup.ModifiedDate = DateTime.Now;
        }
    }

    private void TagCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && e.AddedItems.Count > 0) _vm.CurrentTool = ToolKind.Tag;
    }

    private void StampCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && e.AddedItems.Count > 0) _vm.CurrentTool = ToolKind.Stamp;
    }

    // =================================================================== thumbnails
    private void ThumbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ThumbList.SelectedItem is not ThumbnailItem t) return;
        if (t.Index != Canvas.PageIndex) Canvas.GoToPage(t.Index);
    }

    private ThumbnailItem? ThumbFromElement(object? source)
    {
        if (source is not DependencyObject d) return null;
        var container = ItemsControl.ContainerFromElement(ThumbList, d) as ListBoxItem;
        return container?.DataContext as ThumbnailItem;
    }

    private void Thumb_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _thumbDragStart = e.GetPosition(ThumbList);
        _thumbDragItem = ThumbFromElement(e.OriginalSource);
    }

    private void Thumb_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _thumbDragItem == null) return;
        var d = e.GetPosition(ThumbList) - _thumbDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _thumbDragItem;
        _thumbDragItem = null;
        try { DragDrop.DoDragDrop(ThumbList, new DataObject(typeof(ThumbnailItem), item), DragDropEffects.Move); }
        catch (Exception ex) { Logger.Warn("Thumbnail drag: " + ex.Message); }
    }

    private async void Thumb_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ThumbnailItem))) return; // let file drops bubble to the window
        e.Handled = true;
        if (e.Data.GetData(typeof(ThumbnailItem)) is not ThumbnailItem src) return;
        var target = ThumbFromElement(e.OriginalSource);
        int to = target?.Index ?? _vm.Thumbnails.Count - 1;
        if (to == src.Index) return;
        await _vm.PageOperationAsync($"move:{src.Index}:{to}");
    }

    private int ContextPage => (ThumbList.SelectedItem as ThumbnailItem)?.Index ?? Canvas.PageIndex;
    private async void PageRotate_Click(object sender, RoutedEventArgs e) => await _vm.PageOperationAsync($"rotate:{ContextPage}");
    private async void PageDuplicate_Click(object sender, RoutedEventArgs e) => await _vm.PageOperationAsync($"duplicate:{ContextPage}");
    private async void PageDelete_Click(object sender, RoutedEventArgs e) => await _vm.PageOperationAsync($"delete:{ContextPage}");

    // =================================================================== left panel
    private void Bookmarks_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is BookmarkItem b && b.Page >= 0) GoToPage(b.Page);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (_vm.SearchCommand.CanExecute(null)) _vm.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private void SearchHits_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is SearchHit h) _vm.GoToSearchHitCommand.Execute(h);
    }

    private void SearchHits_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is SearchHit h) _vm.GoToSearchHitCommand.Execute(h);
    }

    // =================================================================== bottom grids
    /// <summary>True if the double-click hit a data row (not the header / scrollbar).</summary>
    private static bool IsRowClick(MouseButtonEventArgs e)
        => e.OriginalSource is DependencyObject d && FindAncestor<DataGridRow>(d) != null;

    private void MarkupGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsRowClick(e)) return;
        if (MarkupGrid.SelectedItem is Markup m) { _vm.ZoomToMarkup(m); e.Handled = true; }
    }

    private void IssueGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsRowClick(e)) return;
        if ((sender as DataGrid)?.SelectedItem is Issue i) _vm.GoToIssueCommand.Execute(i);
    }

    private void RegisterGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsRowClick(e)) return;
        if ((sender as DataGrid)?.SelectedItem is DrawingSheet d) GoToPage(d.Page);
    }

    private void ZoneResults_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsRowClick(e) || (sender as DataGrid)?.SelectedItem is not ZoneResult r) return;
        // results from batch/other files may point to pages beyond this document
        if (Canvas.Document == null || r.Page >= Canvas.Document.PageCount) return;
        var z = _vm.Zones.FirstOrDefault(x => x.Name == r.Zone);
        if (z != null) ZoomToRect(r.Page, z.Rect.Inflate(Math.Max(30, Math.Max(z.Rect.Width, z.Rect.Height) * 0.5)));
        else GoToPage(r.Page);
    }

    private void Changes_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsRowClick(e)) return;
        if ((sender as DataGrid)?.SelectedItem is ChangeRegion c) _vm.GoToChangeCommand.Execute(c);
    }
}