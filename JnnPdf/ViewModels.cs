// ============================================================================
//  ViewModels.cs  –  VIEWMODEL LAYER (MVVM)
//  MainViewModel: document/project state, commands, markups list filtering,
//  undo/redo, autosave, compare/overlay, takeoff, QA/QC, zones, register,
//  search, OCR, export/report. Talks to the view only through IViewer.
// ============================================================================
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JnnPdf;

/// <summary>Simple ICommand implementation.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;
    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _run = run; _can = can; }
    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can == null ? null : _ => can()) { }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p)
    {
        try { _run(p); }
        catch (Exception ex) { Logger.Error("Command failed", ex); Dialogs.Error(ex.Message); }
    }
}

/// <summary>View abstraction used by the ViewModel (implemented by MainWindow).</summary>
public interface IViewer
{
    int CurrentPage { get; }
    void ShowDocument(PdfDocument? doc);
    void SetCompareDocument(PdfDocument? doc);
    void GoToPage(int page);
    void ZoomToRect(int page, PdfRect rect);
    void ViewCommand(string cmd);
    void Refresh();
}

/// <summary>Lazy thumbnail item (rendered only when it becomes visible).</summary>
public sealed class ThumbnailItem : ObservableObject
{
    private readonly PdfDocument _doc;
    private readonly CancellationToken _ct;
    private BitmapSource? _image;
    private bool _requested;
    private string _sheet = "", _revision = "";
    public int Index { get; }
    public string Label => $"{Index + 1}";
    public string Sheet { get => _sheet; set => Set(ref _sheet, value); }
    public string Revision { get => _revision; set => Set(ref _revision, value); }

    public ThumbnailItem(PdfDocument doc, int index, CancellationToken ct) { _doc = doc; Index = index; _ct = ct; }

    public BitmapSource? Image
    {
        get
        {
            if (!_requested) { _requested = true; _ = LoadAsync(); }
            return _image;
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            await Task.Delay(30, _ct); // let virtualization settle while scrolling fast
            var bmp = await Task.Run(() => _doc.RenderThumbnail(Index, 170), _ct);
            _image = bmp;
            OnPropertyChanged(nameof(Image));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Warn($"Thumbnail {Index + 1}: {ex.Message}"); }
    }
}

/// <summary>Snapshot of the editable state for undo/redo.</summary>
public sealed class EditState
{
    public List<Markup> Markups { get; set; } = new();
    public List<Issue> Issues { get; set; } = new();
    public List<Zone> Zones { get; set; } = new();
    public List<DrawingSheet> Register { get; set; } = new();
}

public sealed class MainViewModel : ObservableObject
{
    // ------------------------------------------------------------------ services
    public DatabaseService Db { get; } = new();
    private readonly UndoService _undo = new();
    public IViewer? Viewer { get; set; }

    // ------------------------------------------------------------------ state
    private PdfDocument? _doc, _docB;
    private ProjectData _project = new();
    private string? _projectPath;
    private bool _isDirty, _pdfModified;
    private string _lastSnapshot = "";
    private bool _suppress;
    private readonly DispatcherTimer _commitTimer, _autosaveTimer;
    private CancellationTokenSource _docCts = new();
    private string _lastAutosaveSnapshot = "";

    public PdfDocument? Doc { get => _doc; private set { Set(ref _doc, value); OnPropertyChanged(nameof(HasDocument)); OnPropertyChanged(nameof(WindowTitle)); } }
    public PdfDocument? DocB { get => _docB; private set { Set(ref _docB, value); OnPropertyChanged(nameof(HasCompare)); OnPropertyChanged(nameof(CompareFileName)); } }
    public bool HasDocument => _doc != null;
    public bool HasCompare => _docB != null;
    public string CompareFileName => _docB == null ? "(PDF 2 not open)" : Path.GetFileName(_docB.FilePath);
    public ProjectData Project => _project;
    public bool IsDirty { get => _isDirty; set { if (Set(ref _isDirty, value)) OnPropertyChanged(nameof(WindowTitle)); } }
    public string WindowTitle => _doc == null ? "JNN PDF – PDF Review & Construction Drawing Tool"
        : $"JNN PDF – {Path.GetFileName(_doc.FilePath)}{(_projectPath != null ? "  [" + Path.GetFileName(_projectPath) + "]" : "")}{(IsDirty ? " *" : "")}";

    public ObservableCollection<Markup> Markups { get; } = new();
    public ObservableCollection<Issue> Issues { get; } = new();
    public ObservableCollection<Zone> Zones { get; } = new();
    public ObservableCollection<DrawingSheet> Register { get; } = new();
    public ObservableCollection<ZoneResult> ZoneResults { get; } = new();
    public ObservableCollection<ChangeRegion> Changes { get; } = new();
    public ObservableCollection<SearchHit> SearchHits { get; } = new();
    public ObservableCollection<ThumbnailItem> Thumbnails { get; } = new();
    public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();
    public ObservableCollection<TakeoffSummary> Takeoff { get; } = new();
    public ObservableCollection<string> RecentFiles { get; } = new();
    public ObservableCollection<KeyValuePair<string, string>> DocInfo { get; } = new();

    public ICollectionView MarkupsView { get; }
    public ICollectionView IssuesView { get; }
    public ICollectionView RegisterView { get; }

    // ------------------------------------------------------------------ selection
    private Markup? _selectedMarkup;
    public Markup? SelectedMarkup { get => _selectedMarkup; set { if (Set(ref _selectedMarkup, value)) OnPropertyChanged(nameof(HasSelection)); } }
    public bool HasSelection => _selectedMarkup != null;
    private Issue? _selectedIssue;
    public Issue? SelectedIssue { get => _selectedIssue; set => Set(ref _selectedIssue, value); }
    private Zone? _selectedZone;
    public Zone? SelectedZone { get => _selectedZone; set => Set(ref _selectedZone, value); }

    // ------------------------------------------------------------------ tool + style
    private ToolKind _tool = ToolKind.Select;
    public ToolKind CurrentTool { get => _tool; set { if (Set(ref _tool, value)) OnPropertyChanged(nameof(ToolName)); } }
    public string ToolName => _tool.ToString();
    private string _color = "#E53935";
    public string CurrentColor { get => _color; set => Set(ref _color, value); }
    private double _lineWidth = 2;
    public double CurrentLineWidth { get => _lineWidth; set => Set(ref _lineWidth, value); }
    private double _fontSize = 12;
    public double CurrentFontSize { get => _fontSize; set => Set(ref _fontSize, value); }
    private string _tag = "RFI";
    public string CurrentTag { get => _tag; set => Set(ref _tag, value); }
    private string _stamp = "APPROVED";
    public string CurrentStamp { get => _stamp; set => Set(ref _stamp, value); }
    public string[] Palette { get; } = { "#E53935", "#FB8C00", "#FDD835", "#43A047", "#00ACC1", "#1E88E5", "#8E24AA", "#000000", "#757575" };

    // ------------------------------------------------------------------ takeoff
    private bool _takeoffActive;
    public bool TakeoffActive { get => _takeoffActive; set => Set(ref _takeoffActive, value); }
    private string _takeoffCategory = "Concrete";
    public string TakeoffCategory { get => _takeoffCategory; set => Set(ref _takeoffCategory, value); }
    private string _takeoffName = "";
    public string TakeoffName { get => _takeoffName; set => Set(ref _takeoffName, value); }

    // ------------------------------------------------------------------ list filters
    private string _markupSearch = "", _statusFilter = "All", _typeFilter = "All", _groupBy = "None";
    private bool _currentPageOnly;
    public string MarkupSearch { get => _markupSearch; set { if (Set(ref _markupSearch, value)) MarkupsView.Refresh(); } }
    public string StatusFilter { get => _statusFilter; set { if (Set(ref _statusFilter, value)) MarkupsView.Refresh(); } }
    public string TypeFilter { get => _typeFilter; set { if (Set(ref _typeFilter, value)) MarkupsView.Refresh(); } }
    public bool CurrentPageOnly { get => _currentPageOnly; set { if (Set(ref _currentPageOnly, value)) MarkupsView.Refresh(); } }
    public string GroupBy { get => _groupBy; set { if (Set(ref _groupBy, value)) ApplyGrouping(); } }
    public string[] StatusFilterOptions { get; } = new[] { "All" }.Concat(Lists.MarkupStatuses).ToArray();
    public string[] TypeFilterOptions { get; } = new[] { "All" }.Concat(Enum.GetNames<MarkupType>()).ToArray();
    public string[] GroupOptions { get; } = { "None", "Type", "Page", "Status", "Author", "Subject", "Category" };
    private string _issueFilter = "";
    public string IssueFilter { get => _issueFilter; set { if (Set(ref _issueFilter, value)) IssuesView.Refresh(); } }
    private string _registerFilter = "";
    public string RegisterFilter { get => _registerFilter; set { if (Set(ref _registerFilter, value)) RegisterView.Refresh(); } }

    // ------------------------------------------------------------------ status bar
    private string _pageText = "Page: – / –", _zoomText = "Zoom: –", _coordText = "", _measureText = "", _status = "Ready";
    public string PageText { get => _pageText; set => Set(ref _pageText, value); }
    public string ZoomText { get => _zoomText; set => Set(ref _zoomText, value); }
    public string ScaleText => "Scale: " + ScaleOf(Viewer?.CurrentPage ?? 0).Label;
    public string CoordText { get => _coordText; set => Set(ref _coordText, value); }
    public string MeasureText { get => _measureText; set => Set(ref _measureText, value); }
    public string StatusMessage { get => _status; set => Set(ref _status, value); }
    private bool _busy;
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }
    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    private CancellationTokenSource? _jobCts;

    // ------------------------------------------------------------------ region read / search
    private string _regionA = "", _regionB = "", _regionDiff = "";
    public string RegionTextA { get => _regionA; set => Set(ref _regionA, value); }
    public string RegionTextB { get => _regionB; set => Set(ref _regionB, value); }
    public string RegionDiff { get => _regionDiff; set => Set(ref _regionDiff, value); }
    private string _searchText = "";
    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    private bool _searchRegex;
    public bool SearchRegex { get => _searchRegex; set => Set(ref _searchRegex, value); }

    // ------------------------------------------------------------------ compare / overlay
    private OverlayMode _overlayMode = OverlayMode.None;
    public OverlayMode OverlayMode { get => _overlayMode; set => Set(ref _overlayMode, value); }
    public OverlayMode[] OverlayModes { get; } = Enum.GetValues<OverlayMode>();
    private double _overlayOpacity = 50, _ovX, _ovY, _ovRot, _ovScale = 1;
    public double OverlayOpacity { get => _overlayOpacity; set => Set(ref _overlayOpacity, value); }
    public double OverlayOffsetX { get => _ovX; set => Set(ref _ovX, value); }
    public double OverlayOffsetY { get => _ovY; set => Set(ref _ovY, value); }
    public double OverlayRotation { get => _ovRot; set => Set(ref _ovRot, value); }
    public double OverlayScale { get => _ovScale; set => Set(ref _ovScale, value); }
    private int _currentChange = -1;
    public int CurrentChange { get => _currentChange; set { if (Set(ref _currentChange, value)) OnPropertyChanged(nameof(ChangeText)); } }
    public string ChangeText => Changes.Count == 0 ? "No changes" : $"Change {CurrentChange + 1} / {Changes.Count}";

    // ------------------------------------------------------------------ theme
    private bool _isDark = true;
    public bool IsDark { get => _isDark; set { if (Set(ref _isDark, value)) { App.ApplyTheme(value); Db.Set("theme", value ? "dark" : "light"); } } }
    public string Author { get => _project.User.Name; set { _project.User.Name = value; Db.Set("author", value); OnPropertyChanged(); } }

    // ------------------------------------------------------------------ static lists for XAML
    public string[] MarkupStatuses => Lists.MarkupStatuses;
    public string[] IssueStatuses => Lists.IssueStatuses;
    public string[] Severities => Lists.Severities;
    public string[] IssueCategories => Lists.IssueCategories;
    public string[] TagList => Lists.Tags;
    public string[] StampList => Lists.Stamps;
    public string[] ZoneRules => Lists.ZoneRules;
    public string[] TakeoffCategories => Lists.TakeoffCategories;
    public string[] DrawingStatuses => Lists.DrawingStatuses;

    // ------------------------------------------------------------------ commands
    public ICommand NewCommand { get; }
    public ICommand OpenPdfCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand OpenRecentCommand { get; }
    public ICommand CreateProjectCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand SavePdfAsCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand DeleteMarkupCommand { get; }
    public ICommand DuplicateMarkupCommand { get; }
    public ICommand ToolCommand { get; }
    public ICommand ViewCommand { get; }
    public ICommand SetScaleCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand GoToSearchHitCommand { get; }
    public ICommand ZoomToMarkupCommand { get; }
    public ICommand OpenCompareCommand { get; }
    public ICommand CloseCompareCommand { get; }
    public ICommand RunCompareCommand { get; }
    public ICommand RunComparePageCommand { get; }
    public ICommand NextChangeCommand { get; }
    public ICommand PrevChangeCommand { get; }
    public ICommand ExportComparePdfCommand { get; }
    public ICommand ResetOverlayCommand { get; }
    public ICommand CancelJobCommand { get; }
    public ICommand ExportMarkupsExcelCommand { get; }
    public ICommand ExportMarkupsCsvCommand { get; }
    public ICommand ExportTakeoffCommand { get; }
    public ICommand ExportIssuesCommand { get; }
    public ICommand ExportRegisterCommand { get; }
    public ICommand ExportZoneResultsCommand { get; }
    public ICommand ExportFlattenedPdfCommand { get; }
    public ICommand ExportPngCommand { get; }
    public ICommand ReportCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand NewIssueCommand { get; }
    public ICommand IssueFromMarkupCommand { get; }
    public ICommand DeleteIssueCommand { get; }
    public ICommand AdvanceIssueCommand { get; }
    public ICommand GoToIssueCommand { get; }
    public ICommand RunZonesCommand { get; }
    public ICommand DeleteZoneCommand { get; }
    public ICommand BatchCommand { get; }
    public ICommand DetectRegisterCommand { get; }
    public ICommand GoToRegisterCommand { get; }
    public ICommand OcrPageCommand { get; }
    public ICommand OcrAllCommand { get; }
    public ICommand ExportSearchableCommand { get; }
    public ICommand PageOpCommand { get; }
    public ICommand CopyRegionCommand { get; }
    public ICommand ClearRecentCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand GoToChangeCommand { get; }
    public ICommand SetTagCommand { get; }
    public ICommand SetStampCommand { get; }

    public MainViewModel()
    {
        MarkupsView = CollectionViewSource.GetDefaultView(Markups);
        MarkupsView.Filter = FilterMarkup;
        IssuesView = CollectionViewSource.GetDefaultView(Issues);
        IssuesView.Filter = o => o is Issue i && (string.IsNullOrWhiteSpace(IssueFilter) ||
            $"{i.IssueId} {i.Sheet} {i.Category} {i.Description} {i.Severity} {i.AssignedTo} {i.Status} {i.Reviewer}".Contains(IssueFilter, StringComparison.OrdinalIgnoreCase));
        RegisterView = CollectionViewSource.GetDefaultView(Register);
        RegisterView.Filter = o => o is DrawingSheet d && (string.IsNullOrWhiteSpace(RegisterFilter) ||
            $"{d.Sheet} {d.Title} {d.Revision} {d.Date} {d.Status}".Contains(RegisterFilter, StringComparison.OrdinalIgnoreCase));

        Markups.CollectionChanged += (_, e) => HookItems(e);
        Issues.CollectionChanged += (_, e) => HookItems(e);
        Zones.CollectionChanged += (_, e) => HookItems(e);
        Register.CollectionChanged += (_, e) => HookItems(e);
        Changes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ChangeText));

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _commitTimer.Tick += (_, _) => { _commitTimer.Stop(); Commit(); };
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _autosaveTimer.Tick += (_, _) => Autosave();
        _autosaveTimer.Start();

        _isDark = Db.Get("theme", "dark") != "light";
        _project.User.Name = Db.Get("author", Environment.UserName);
        LoadRecent();

        NewCommand = new RelayCommand(NewProject);
        OpenPdfCommand = new RelayCommand(async () => { var f = Dialogs.OpenFile(Dialogs.PdfFilter, "Open PDF"); if (f != null) await OpenPdfAsync(f); });
        OpenProjectCommand = new RelayCommand(async () => { var f = Dialogs.OpenFile(Dialogs.ProjectFilter, "Open project"); if (f != null) await OpenProjectAsync(f); });
        OpenRecentCommand = new RelayCommand(async p => { if (p is string f) await OpenAnyAsync(f); });
        CreateProjectCommand = new RelayCommand(async () =>
        {
            var pdf = Dialogs.OpenFile(Dialogs.PdfFilter, "Choose a PDF for the new project");
            if (pdf == null) return;
            var proj = Dialogs.SaveFile(Dialogs.ProjectFilter, Path.GetFileNameWithoutExtension(pdf) + ProjectService.Extension, "Save new project");
            if (proj == null) return;
            if (await OpenPdfAsync(pdf)) { _project.Name = Path.GetFileNameWithoutExtension(proj); SaveProjectTo(proj); }
        });
        SaveCommand = new RelayCommand(() => Save(false), () => HasDocument);
        SaveAsCommand = new RelayCommand(() => Save(true), () => HasDocument);
        SavePdfAsCommand = new RelayCommand(() => SavePdfAs(), () => HasDocument);
        CloseCommand = new RelayCommand(() => CloseDocument(), () => HasDocument);
        UndoCommand = new RelayCommand(Undo, () => _undo.CanUndo);
        RedoCommand = new RelayCommand(Redo, () => _undo.CanRedo);
        DeleteMarkupCommand = new RelayCommand(p => DeleteMarkups(p), _ => SelectedMarkup != null);
        DuplicateMarkupCommand = new RelayCommand(DuplicateMarkup, () => SelectedMarkup != null);
        ToolCommand = new RelayCommand(p => { if (p is string s && Enum.TryParse<ToolKind>(s, out var t)) CurrentTool = t; else if (p is ToolKind tk) CurrentTool = tk; });
        ViewCommand = new RelayCommand(p => Viewer?.ViewCommand(p as string ?? ""), _ => HasDocument);
        SetScaleCommand = new RelayCommand(SetScale, () => HasDocument);
        SearchCommand = new RelayCommand(async () => await SearchAsync(), () => HasDocument);
        GoToSearchHitCommand = new RelayCommand(p => { if (p is SearchHit h) Viewer?.ZoomToRect(h.Page, h.Rect.Inflate(40)); });
        ZoomToMarkupCommand = new RelayCommand(p => ZoomToMarkup(p as Markup ?? SelectedMarkup));
        OpenCompareCommand = new RelayCommand(async () => { var f = Dialogs.OpenFile(Dialogs.PdfFilter, "Open PDF 2 (new revision / overlay)"); if (f != null) await OpenCompareAsync(f); }, () => HasDocument);
        CloseCompareCommand = new RelayCommand(CloseCompare, () => HasCompare);
        RunCompareCommand = new RelayCommand(async () => await RunCompareAsync(true), () => HasCompare && !IsBusy);
        RunComparePageCommand = new RelayCommand(async () => await RunCompareAsync(false), () => HasCompare && !IsBusy);
        NextChangeCommand = new RelayCommand(() => StepChange(1), () => Changes.Count > 0);
        PrevChangeCommand = new RelayCommand(() => StepChange(-1), () => Changes.Count > 0);
        GoToChangeCommand = new RelayCommand(p => { if (p is ChangeRegion c) { CurrentChange = Changes.IndexOf(c); ShowChange(); } });
        ExportComparePdfCommand = new RelayCommand(ExportComparePdf, () => HasCompare && Changes.Count > 0);
        ResetOverlayCommand = new RelayCommand(() => { OverlayOffsetX = OverlayOffsetY = OverlayRotation = 0; OverlayScale = 1; });
        CancelJobCommand = new RelayCommand(() => _jobCts?.Cancel(), () => IsBusy);
        ExportMarkupsExcelCommand = new RelayCommand(() => ExportTable(ExportService.MarkupsTable(MarkupsView.Cast<Markup>()), true), () => HasDocument);
        ExportMarkupsCsvCommand = new RelayCommand(() => ExportTable(ExportService.MarkupsTable(MarkupsView.Cast<Markup>()), false), () => HasDocument);
        ExportTakeoffCommand = new RelayCommand(() => { RefreshTakeoff(); ExportTables(true, ExportService.TakeoffTable(Takeoff), ExportService.MeasurementsTable(Markups)); }, () => HasDocument);
        ExportIssuesCommand = new RelayCommand(() => ExportTable(ExportService.IssuesTable(IssuesView.Cast<Issue>()), true), () => HasDocument);
        ExportRegisterCommand = new RelayCommand(p => ExportTable(ExportService.RegisterTable(RegisterView.Cast<DrawingSheet>()), (p as string) != "csv"), _ => HasDocument);
        ExportZoneResultsCommand = new RelayCommand(() => ExportTable(ExportService.ZoneResultsTable(ZoneResults), true), () => ZoneResults.Count > 0);
        ExportFlattenedPdfCommand = new RelayCommand(ExportFlattenedPdf, () => HasDocument);
        ExportPngCommand = new RelayCommand(ExportPng, () => HasDocument);
        ReportCommand = new RelayCommand(ExportReport, () => HasDocument);
        PrintCommand = new RelayCommand(() => { if (_doc != null) ExportService.Print(_doc, Markups, Viewer?.CurrentPage ?? 0); }, () => HasDocument);
        NewIssueCommand = new RelayCommand(() => CreateIssue(null), () => HasDocument);
        IssueFromMarkupCommand = new RelayCommand(() => CreateIssue(SelectedMarkup), () => SelectedMarkup != null);
        DeleteIssueCommand = new RelayCommand(() => { if (SelectedIssue != null) { Issues.Remove(SelectedIssue); CommitNow(); } }, () => SelectedIssue != null);
        AdvanceIssueCommand = new RelayCommand(() =>
        {
            if (SelectedIssue == null) return;
            int i = Array.IndexOf(Lists.IssueStatuses, SelectedIssue.Status);
            if (i < Lists.IssueStatuses.Length - 1) SelectedIssue.Status = Lists.IssueStatuses[i + 1];
        }, () => SelectedIssue != null);
        GoToIssueCommand = new RelayCommand(p =>
        {
            var i = p as Issue ?? SelectedIssue; if (i == null) return;
            var m = i.MarkupId == null ? null : Markups.FirstOrDefault(x => x.Id == i.MarkupId);
            if (m != null) ZoomToMarkup(m); else Viewer?.ZoomToRect(i.Page, new PdfRect(i.X - 100, i.Y - 100, 200, 200));
        });
        RunZonesCommand = new RelayCommand(async () => await RunZonesAsync(), () => HasDocument && Zones.Count > 0 && !IsBusy);
        DeleteZoneCommand = new RelayCommand(() => { if (SelectedZone != null) { Zones.Remove(SelectedZone); CommitNow(); Viewer?.Refresh(); } }, () => SelectedZone != null);
        BatchCommand = new RelayCommand(() => new BatchWindow(() => Zones.ToList()).Show());
        DetectRegisterCommand = new RelayCommand(async () => await DetectRegisterAsync(true), () => HasDocument && !IsBusy);
        GoToRegisterCommand = new RelayCommand(p => { if (p is DrawingSheet d) Viewer?.GoToPage(d.Page); });
        OcrPageCommand = new RelayCommand(async () => await OcrAsync(false), () => HasDocument && !IsBusy);
        OcrAllCommand = new RelayCommand(async () => await OcrAsync(true), () => HasDocument && !IsBusy);
        ExportSearchableCommand = new RelayCommand(ExportSearchable, () => HasDocument && _project.OcrText.Count > 0);
        PageOpCommand = new RelayCommand(async p => await PageOperationAsync(p as string ?? ""), _ => HasDocument && !IsBusy);
        CopyRegionCommand = new RelayCommand(() => { try { Clipboard.SetText(RegionTextA + (string.IsNullOrEmpty(RegionTextB) ? "" : "\n---- PDF 2 ----\n" + RegionTextB)); StatusMessage = "Copied."; } catch { } });
        ClearRecentCommand = new RelayCommand(() => { Db.ClearRecent(); LoadRecent(); });
        ToggleThemeCommand = new RelayCommand(() => IsDark = !IsDark);
        SetTagCommand = new RelayCommand(p => { if (p is string s) { CurrentTag = s; CurrentTool = ToolKind.Tag; } });
        SetStampCommand = new RelayCommand(p => { if (p is string s) { CurrentStamp = s; CurrentTool = ToolKind.Stamp; } });
    }

    // =================================================================== helpers
    public ScaleSetting ScaleOf(int page) => _project.PageScales.TryGetValue(page, out var s) ? s : _project.DefaultScale;
    public IReadOnlyList<TextWord> OcrWords(int page)
        => _project.OcrText.TryGetValue(page, out var w) ? w.Select(x => new TextWord(x.Text, x.Rect)).ToList() : Array.Empty<TextWord>();

    private void LoadRecent()
    {
        RecentFiles.Clear();
        foreach (var f in Db.GetRecent()) RecentFiles.Add(f);
    }

    private bool FilterMarkup(object o)
    {
        if (o is not Markup m) return false;
        if (StatusFilter != "All" && m.Status != StatusFilter) return false;
        if (TypeFilter != "All" && m.Type.ToString() != TypeFilter) return false;
        if (CurrentPageOnly && Viewer != null && m.Page != Viewer.CurrentPage) return false;
        if (!string.IsNullOrWhiteSpace(MarkupSearch))
        {
            string hay = $"{m.Id} {m.Type} {m.Subject} {m.Text} {m.Comment} {m.Author} {m.Status} {m.Category} {m.ValueText}";
            if (!hay.Contains(MarkupSearch, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private void ApplyGrouping()
    {
        MarkupsView.GroupDescriptions.Clear();
        string? prop = GroupBy switch { "Type" => "Type", "Page" => "PageNumber", "Status" => "Status", "Author" => "Author", "Subject" => "Subject", "Category" => "Category", _ => null };
        if (prop != null) MarkupsView.GroupDescriptions.Add(new PropertyGroupDescription(prop));
    }

    public void OnPageChanged(int page, int count, double zoom)
    {
        PageText = $"Page: {page + 1} / {count}";
        ZoomText = $"Zoom: {zoom * 72 / 96 * 100:0}%";
        OnPropertyChanged(nameof(ScaleText));
        if (CurrentPageOnly) MarkupsView.Refresh();
    }

    private void HookItems(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (INotifyPropertyChanged o in e.OldItems) o.PropertyChanged -= OnItemChanged;
        if (e.NewItems != null) foreach (INotifyPropertyChanged o in e.NewItems) { o.PropertyChanged -= OnItemChanged; o.PropertyChanged += OnItemChanged; }
    }

    private static readonly HashSet<string> IgnoredProps = new() { "Value", "ValueText", "ModifiedDate", "Bounds", "X", "Y", "Width", "Height", "PageNumber", "IsMeasurement" };

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppress || e.PropertyName == null || IgnoredProps.Contains(e.PropertyName)) return;
        if (sender is Markup m && (e.PropertyName is "Points" or "Depth" or "Type")) MeasurementService.Update(m, ScaleOf(m.Page));
        if (sender is Zone) Viewer?.Refresh();
        _commitTimer.Stop(); _commitTimer.Start();
    }

    // =================================================================== undo / redo
    private string Snapshot() => JsonSerializer.Serialize(new EditState
    {
        Markups = Markups.ToList(), Issues = Issues.ToList(), Zones = Zones.ToList(), Register = Register.ToList()
    }, ProjectService.Json);

    /// <summary>Records the current state as an undo step (state before = last snapshot).</summary>
    public void Commit()
    {
        if (_suppress) return;
        _commitTimer.Stop();
        string now = Snapshot();
        if (now == _lastSnapshot) return;
        _undo.Push(_lastSnapshot);
        _lastSnapshot = now;
        IsDirty = true;
        RefreshTakeoff();
        CommandManager.InvalidateRequerySuggested();
    }

    public void CommitNow() => Commit();

    private void Restore(string json)
    {
        _suppress = true;
        try
        {
            var st = JsonSerializer.Deserialize<EditState>(json, ProjectService.Json) ?? new EditState();
            int? selId = SelectedMarkup?.Id;
            Markups.Clear(); foreach (var m in st.Markups) { MeasurementService.Update(m, ScaleOf(m.Page)); Markups.Add(m); }
            Issues.Clear(); foreach (var i in st.Issues) Issues.Add(i);
            Zones.Clear(); foreach (var z in st.Zones) Zones.Add(z);
            Register.Clear(); foreach (var d in st.Register) Register.Add(d);
            SelectedMarkup = selId == null ? null : Markups.FirstOrDefault(m => m.Id == selId);
        }
        finally { _suppress = false; }
        _lastSnapshot = json;
        IsDirty = true;
        RefreshTakeoff();
        Viewer?.Refresh();
    }

    private void Undo()
    {
        Commit(); // flush pending edits first
        var s = _undo.Undo(_lastSnapshot);
        if (s != null) { Restore(s); StatusMessage = "Undo"; }
    }

    private void Redo()
    {
        var s = _undo.Redo(_lastSnapshot);
        if (s != null) { Restore(s); StatusMessage = "Redo"; }
    }

    // =================================================================== open / close
    public async Task OpenAnyAsync(string file)
    {
        if (!File.Exists(file)) { Dialogs.Error($"File does not exist:\n{file}"); return; }
        if (file.EndsWith(ProjectService.Extension, StringComparison.OrdinalIgnoreCase)) await OpenProjectAsync(file);
        else await OpenPdfAsync(file);
    }

    /// <summary>Asks to save pending changes. Returns false if the user cancelled.</summary>
    public bool ConfirmClose()
    {
        Commit();
        if (!IsDirty || _doc == null) return true;
        var r = Dialogs.YesNoCancel("The project has unsaved changes. Save before closing?");
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return Save(false);
        return true;
    }

    private static async Task<PdfDocument?> OpenDocumentAsync(string path)
    {
        string? password = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try { return await Task.Run(() => PdfDocument.Open(path, password)); }
            catch (PdfOpenException ex) when (ex.Kind == PdfErrorKind.Password)
            {
                password = Dialogs.Password(path);
                if (password == null) return null;
            }
        }
        Dialogs.Error("Too many wrong password attempts.");
        return null;
    }

    public async Task<bool> OpenPdfAsync(string path, ProjectData? project = null, string? projectPath = null)
    {
        if (project == null && !ConfirmClose()) return false;
        try
        {
            StatusMessage = "Opening " + Path.GetFileName(path) + "…";
            IsBusy = true;
            var doc = await OpenDocumentAsync(path);
            if (doc == null) { StatusMessage = "Cancelled."; return false; }
            CloseDocument(force: true);
            _docCts = new CancellationTokenSource();
            Doc = doc;
            _project = project ?? new ProjectData { Name = Path.GetFileNameWithoutExtension(path), User = new UserInfo { Name = Author } };
            _project.PdfPath = path;
            _projectPath = projectPath;
            _pdfModified = false;
            LoadProjectCollections();
            BuildThumbnails();
            Viewer?.ShowDocument(doc);
            OnPropertyChanged(nameof(ScaleText)); OnPropertyChanged(nameof(Project)); OnPropertyChanged(nameof(WindowTitle));
            DocInfo.Clear();
            DocInfo.Add(new("File", Path.GetFileName(path)));
            DocInfo.Add(new("Pages", doc.PageCount.ToString()));
            DocInfo.Add(new("Size", $"{doc.Info.FileSize / 1024.0 / 1024.0:0.0} MB"));
            var s0 = doc.GetPageSize(0);
            DocInfo.Add(new("Page 1", $"{s0.Width:0} × {s0.Height:0} pt ({s0.Width * 25.4 / 72:0} × {s0.Height * 25.4 / 72:0} mm)"));
            foreach (var kv in doc.Info.Metadata) DocInfo.Add(new(kv.Key, kv.Value));
            Db.AddRecent(projectPath ?? path, projectPath != null ? "project" : "pdf");
            LoadRecent();
            _ = LoadBookmarksAsync(doc);
            if (Register.Count == 0) _ = DetectRegisterAsync(false);
            else ApplyRegisterToThumbnails();
            if (!string.IsNullOrEmpty(_project.ComparePdfPath) && File.Exists(_project.ComparePdfPath)) await OpenCompareAsync(_project.ComparePdfPath);
            StatusMessage = $"Opened {Path.GetFileName(path)} – {doc.PageCount} pages";
            Logger.Info($"Opened {path} ({doc.PageCount} pages)");
            return true;
        }
        catch (PdfOpenException ex) { Dialogs.Error(ex.Message); StatusMessage = ex.Message; return false; }
        catch (Exception ex) { Logger.Error("Open PDF", ex); Dialogs.Error("Cannot open the PDF:\n" + ex.Message); return false; }
        finally { IsBusy = false; }
    }

    private void LoadProjectCollections()
    {
        _suppress = true;
        try
        {
            Markups.Clear(); Issues.Clear(); Zones.Clear(); Register.Clear(); ZoneResults.Clear(); Changes.Clear(); SearchHits.Clear();
            foreach (var m in _project.Markups) { MeasurementService.Update(m, ScaleOf(m.Page)); Markups.Add(m); }
            foreach (var i in _project.Issues) Issues.Add(i);
            foreach (var z in _project.Zones) Zones.Add(z);
            foreach (var d in _project.Register) Register.Add(d);
        }
        finally { _suppress = false; }
        _undo.Clear();
        _lastSnapshot = Snapshot();
        _lastAutosaveSnapshot = _lastSnapshot;
        IsDirty = false;
        RefreshTakeoff();
    }

    public async Task OpenProjectAsync(string path)
    {
        if (!ConfirmClose()) return;
        try
        {
            var p = ProjectService.Load(path);
            if (!File.Exists(p.PdfPath))
            {
                Dialogs.Info($"The project PDF was not found:\n{p.PdfPath}\n\nPlease locate the PDF file.");
                var f = Dialogs.OpenFile(Dialogs.PdfFilter, "Choose the PDF for this project");
                if (f == null) return;
                p.PdfPath = f;
            }
            await OpenPdfAsync(p.PdfPath, p, path);
        }
        catch (JsonException ex) { Dialogs.Error("The project file is malformed:\n" + ex.Message); }
        catch (Exception ex) { Logger.Error("Open project", ex); Dialogs.Error("Cannot open the project:\n" + ex.Message); }
    }

    /// <summary>Restores an autosave/backup after a crash.</summary>
    public async Task RecoverAsync(string backup, string? originalProject)
    {
        try
        {
            var p = ProjectService.Load(backup);
            if (!File.Exists(p.PdfPath)) { Dialogs.Error("The PDF of the recovered project was not found:\n" + p.PdfPath); return; }
            await OpenPdfAsync(p.PdfPath, p, originalProject);
            IsDirty = true;
            StatusMessage = "Recovered from " + Path.GetFileName(backup);
        }
        catch (Exception ex) { Logger.Error("Recover", ex); Dialogs.Error("Recovery failed:\n" + ex.Message); }
    }

    private void BuildThumbnails()
    {
        Thumbnails.Clear();
        if (_doc == null) return;
        for (int i = 0; i < _doc.PageCount; i++) Thumbnails.Add(new ThumbnailItem(_doc, i, _docCts.Token));
    }

    private async Task LoadBookmarksAsync(PdfDocument doc)
    {
        var list = await Task.Run(doc.GetBookmarks);
        if (doc != _doc) return;
        Bookmarks.Clear();
        foreach (var b in list) Bookmarks.Add(b);
    }

    public void CloseDocument(bool force = false)
    {
        if (!force && !ConfirmClose()) return;
        _docCts.Cancel();
        _jobCts?.Cancel();
        Viewer?.SetCompareDocument(null);
        Viewer?.ShowDocument(null);
        DocB?.Dispose(); DocB = null;
        Doc?.Dispose(); Doc = null;
        _suppress = true;
        Markups.Clear(); Issues.Clear(); Zones.Clear(); Register.Clear(); ZoneResults.Clear(); Changes.Clear();
        SearchHits.Clear(); Thumbnails.Clear(); Bookmarks.Clear(); Takeoff.Clear(); DocInfo.Clear();
        _suppress = false;
        SelectedMarkup = null;
        _projectPath = null;
        _project = new ProjectData { User = new UserInfo { Name = Author } };
        _undo.Clear();
        IsDirty = false;
        RegionTextA = RegionTextB = RegionDiff = "";
        PageText = "Page: – / –"; ZoomText = "Zoom: –";
        OnPropertyChanged(nameof(WindowTitle));
        StatusMessage = "Ready";
    }

    private void NewProject()
    {
        if (!ConfirmClose()) return;
        CloseDocument(force: true);
    }

    // =================================================================== save
    private ProjectData CollectProject()
    {
        Commit();
        _project.Markups = Markups.ToList();
        _project.Issues = Issues.ToList();
        _project.Zones = Zones.ToList();
        _project.Register = Register.ToList();
        _project.ComparePdfPath = _docB?.FilePath ?? "";
        return _project;
    }

    /// <summary>Save (or Save As). Returns true on success.</summary>
    public bool Save(bool saveAs)
    {
        if (_doc == null) return false;
        if (_pdfModified && Dialogs.Confirm("Pages of the PDF were edited (delete/rotate/duplicate/reorder).\nSave the new PDF before saving the project?"))
            if (!SavePdfAs()) return false;
        string? path = _projectPath;
        if (saveAs || path == null)
        {
            path = Dialogs.SaveFile(Dialogs.ProjectFilter, (_project.Name.Length > 0 ? _project.Name : Path.GetFileNameWithoutExtension(_doc.FilePath)) + ProjectService.Extension, "Save project");
            if (path == null) return false;
        }
        return SaveProjectTo(path);
    }

    private bool SaveProjectTo(string path)
    {
        try
        {
            ProjectService.Save(path, CollectProject());
            _projectPath = path;
            IsDirty = false;
            Db.AddRecent(path, "project"); LoadRecent();
            OnPropertyChanged(nameof(WindowTitle));
            StatusMessage = "Saved " + Path.GetFileName(path);
            Logger.Info("Saved project " + path);
            return true;
        }
        catch (Exception ex) { Logger.Error("Save", ex); Dialogs.Error("Save failed:\n" + ex.Message); return false; }
    }

    private bool SavePdfAs()
    {
        if (_doc == null) return false;
        var path = Dialogs.SaveFile(Dialogs.PdfFilter, Path.GetFileNameWithoutExtension(_project.PdfPath) + (_pdfModified ? "_edited" : "_copy") + ".pdf", "Save PDF");
        if (path == null) return false;
        try
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_doc.FilePath), StringComparison.OrdinalIgnoreCase)) { Dialogs.Error("Cannot overwrite the open file. Choose another name."); return false; }
            File.Copy(_doc.FilePath, path, true);
            _project.PdfPath = path;
            _pdfModified = false;
            StatusMessage = "Saved PDF: " + path;
            return true;
        }
        catch (Exception ex) { Dialogs.Error("Saving the PDF failed:\n" + ex.Message); return false; }
    }

    /// <summary>Autosave every minute + rolling backups + crash-recovery marker.</summary>
    private void Autosave()
    {
        if (_doc == null) return;
        try
        {
            var snap = Snapshot();
            if (snap == _lastAutosaveSnapshot) return;
            var data = CollectProject();
            string target = ProjectService.AutosavePathFor(_projectPath, _doc.FilePath);
            if (_projectPath != null) { ProjectService.Save(_projectPath, data); IsDirty = false; }
            else ProjectService.Save(target, data);
            string backup = ProjectService.WriteBackup(target, data);
            ProjectService.MarkSession(backup, _projectPath);
            _lastAutosaveSnapshot = snap;
            StatusMessage = $"Autosave {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { Logger.Error("Autosave", ex); StatusMessage = "Autosave error: " + ex.Message; }
    }

    // =================================================================== markups
    /// <summary>Called by the canvas when the user finished drawing a markup.</summary>
    public void AddMarkup(Markup m)
    {
        if (_doc == null) return;
        m.Id = _project.NextMarkupId++;
        m.Author = Author;
        m.CreatedDate = m.ModifiedDate = DateTime.Now;
        var reg = Register.FirstOrDefault(r => r.Page == m.Page);
        switch (m.Type)
        {
            case MarkupType.TextBox or MarkupType.Callout or MarkupType.Typewriter or MarkupType.Label:
                var t = Dialogs.Prompt("Text", "Enter text:", m.Type == MarkupType.Label ? "LABEL" : "", m.Type != MarkupType.Label);
                if (t == null) { _project.NextMarkupId--; return; }
                m.Text = t; break;
            case MarkupType.Hyperlink:
                var target = Dialogs.Prompt("Hyperlink", "Target: page number (e.g. 45), sheet:S-101, URL (https://…) or file path:", "",
                    suggestions: Register.Select(r => "sheet:" + r.Sheet).Concat(Enumerable.Range(1, Math.Min(_doc.PageCount, 200)).Select(i => i.ToString())));
                if (string.IsNullOrWhiteSpace(target)) { _project.NextMarkupId--; return; }
                m.LinkTarget = target.Trim(); m.Text = m.LinkTarget; break;
            case MarkupType.Stamp:
                m.Revision = reg?.Revision ?? ""; break;
            case MarkupType.Tag:
                m.Text = Dialogs.Prompt("Note " + m.Subject, "Text (optional):", "") ?? ""; break;
        }
        if (m.IsMeasurement && TakeoffActive && m.Type != MarkupType.Angle)
        {
            m.Category = TakeoffCategory;
            m.TakeoffName = TakeoffName;
            m.Subject = string.IsNullOrWhiteSpace(TakeoffName) ? TakeoffCategory : TakeoffName;
        }
        MeasurementService.Update(m, ScaleOf(m.Page));
        Markups.Add(m);
        if (m.Type == MarkupType.IssuePin) CreateIssue(m, fromPin: true);
        SelectedMarkup = m;
        CommitNow();
        if (m.IsMeasurement) MeasureText = m.ValueText;
        StatusMessage = $"Created {m.Type} #{m.Id}";
    }

    private void DeleteMarkups(object? p)
    {
        var list = (p as System.Collections.IList)?.OfType<Markup>().ToList() ?? new List<Markup>();
        if (list.Count == 0 && SelectedMarkup != null) list.Add(SelectedMarkup);
        if (list.Count == 0) return;
        foreach (var m in list)
        {
            Markups.Remove(m);
            foreach (var i in Issues.Where(i => i.MarkupId == m.Id)) i.MarkupId = null;
        }
        SelectedMarkup = null;
        CommitNow();
        StatusMessage = $"Deleted {list.Count} markup(s)";
    }

    private void DuplicateMarkup()
    {
        if (SelectedMarkup == null) return;
        var json = JsonSerializer.Serialize(SelectedMarkup, ProjectService.Json);
        var c = JsonSerializer.Deserialize<Markup>(json, ProjectService.Json)!;
        c.Id = _project.NextMarkupId++;
        c.Points = c.Points.Select(q => new PdfPoint(q.X + 15, q.Y + 15)).ToList();
        if (c.Rects != null) c.Rects = c.Rects.Select(r => new PdfRect(r.X + 15, r.Y + 15, r.Width, r.Height)).ToList();
        c.CreatedDate = c.ModifiedDate = DateTime.Now;
        MeasurementService.Update(c, ScaleOf(c.Page));
        Markups.Add(c);
        SelectedMarkup = c;
        CommitNow();
    }

    public void ZoomToMarkup(Markup? m)
    {
        if (m == null) return;
        SelectedMarkup = m;
        Viewer?.ZoomToRect(m.Page, m.Bounds.Inflate(Math.Max(30, Math.Max(m.Bounds.Width, m.Bounds.Height) * 0.3)));
    }

    /// <summary>Follows a hyperlink markup.</summary>
    public async void FollowLink(Markup m)
    {
        string t = (m.LinkTarget ?? "").Trim();
        if (t.Length == 0) return;
        try
        {
            if (int.TryParse(t.Replace("page:", "", StringComparison.OrdinalIgnoreCase), out int page)) { Viewer?.GoToPage(page - 1); return; }
            if (t.StartsWith("sheet:", StringComparison.OrdinalIgnoreCase))
            {
                var s = t[6..].Trim();
                var d = Register.FirstOrDefault(r => string.Equals(r.Sheet, s, StringComparison.OrdinalIgnoreCase));
                if (d != null) Viewer?.GoToPage(d.Page); else Dialogs.Info($"Sheet {s} was not found in the Drawing Register.");
                return;
            }
            if (t.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { Process.Start(new ProcessStartInfo(t) { UseShellExecute = true }); return; }
            if (File.Exists(t))
            {
                if (t.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && Dialogs.Confirm($"Open the PDF in JNN PDF?\n{t}\n\n(No = open with the default application)")) await OpenPdfAsync(t);
                else Process.Start(new ProcessStartInfo(t) { UseShellExecute = true });
                return;
            }
            Dialogs.Error("Invalid link target: " + t);
        }
        catch (Exception ex) { Dialogs.Error("Cannot open the link:\n" + ex.Message); }
    }

    // =================================================================== scale + measurement
    private void SetScale()
    {
        int page = Viewer?.CurrentPage ?? 0;
        var s = Dialogs.ScaleDialog(ScaleOf(page), out bool all);
        if (s == null) return;
        ApplyScale(s, all, page);
    }

    public void ApplyScale(ScaleSetting s, bool allPages, int page)
    {
        if (allPages) { _project.DefaultScale = s; _project.PageScales.Clear(); }
        else _project.PageScales[page] = s;
        foreach (var m in Markups.Where(m => m.IsMeasurement)) MeasurementService.Update(m, ScaleOf(m.Page));
        IsDirty = true;
        RefreshTakeoff();
        OnPropertyChanged(nameof(ScaleText));
        Viewer?.Refresh();
        StatusMessage = "Scale = " + s.Label;
    }

    public void OnCalibration(double lengthPt)
    {
        var r = Dialogs.CalibrateDialog(lengthPt);
        if (r == null) return;
        try
        {
            var s = MeasurementService.Calibrate(lengthPt, r.Value.Length, r.Value.Unit);
            ApplyScale(s, r.Value.AllPages, Viewer?.CurrentPage ?? 0);
        }
        catch (ArgumentException ex) { Dialogs.Error(ex.Message); }
    }

    public void RefreshTakeoff()
    {
        Takeoff.Clear();
        foreach (var t in TakeoffService.Summarize(Markups, ScaleOf)) Takeoff.Add(t);
    }

    // =================================================================== search
    private async Task SearchAsync()
    {
        if (_doc == null || string.IsNullOrWhiteSpace(SearchText)) return;
        SearchHits.Clear();
        var doc = _doc;
        string q = SearchText;
        bool regex = SearchRegex;
        await RunJob("Searching…", async ct =>
        {
            List<SearchHit> hits;
            if (regex)
            {
                var re = new System.Text.RegularExpressions.Regex(q, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                hits = await Task.Run(() =>
                {
                    var r = new List<SearchHit>();
                    for (int p = 0; p < doc.PageCount; p++)
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (var w in doc.GetWords(p).Concat(OcrWords(p)))
                            if (re.IsMatch(w.Text)) r.Add(new SearchHit(p, w.Rect, w.Text));
                        Progress = (p + 1) * 100.0 / doc.PageCount;
                    }
                    return r;
                }, ct);
            }
            else hits = await Task.Run(() => doc.Search(q, ct, new Progress<int>(p => Progress = p * 100.0 / doc.PageCount), OcrWords), ct);
            foreach (var h in hits.Take(5000)) SearchHits.Add(h);
            StatusMessage = $"Found {hits.Count} result(s) for \"{q}\" on {hits.Select(h => h.Page).Distinct().Count()} page(s)";
        });
        Viewer?.Refresh();
        if (SearchHits.Count > 0) Viewer?.ZoomToRect(SearchHits[0].Page, SearchHits[0].Rect.Inflate(40));
    }

    /// <summary>Runs a cancellable background job with busy indicator + error handling.</summary>
    private async Task RunJob(string message, Func<CancellationToken, Task> job)
    {
        _jobCts?.Cancel();
        var cts = _jobCts = new CancellationTokenSource();
        IsBusy = true; Progress = 0; StatusMessage = message;
        try { await job(cts.Token); }
        catch (OperationCanceledException) { StatusMessage = "Stopped."; }
        catch (Exception ex) { Logger.Error(message, ex); Dialogs.Error(ex.Message); StatusMessage = "Error: " + ex.Message; }
        finally { IsBusy = false; Progress = 0; CommandManager.InvalidateRequerySuggested(); }
    }

    // =================================================================== region read / zones / crop
    public void ReadRegion(int page, PdfRect r)
    {
        if (_doc == null) return;
        RegionTextA = _doc.GetTextInRect(page, r, OcrWords(page));
        RegionTextB = _docB != null && page < _docB.PageCount ? _docB.GetTextInRect(Math.Min(page, _docB.PageCount - 1), r) : "";
        if (_docB != null)
        {
            var la = RegionTextA.Split(Environment.NewLine).Select(ZoneService.Normalize).Where(s => s.Length > 0).ToList();
            var lb = RegionTextB.Split(Environment.NewLine).Select(ZoneService.Normalize).Where(s => s.Length > 0).ToList();
            var onlyA = la.Except(lb).ToList(); var onlyB = lb.Except(la).ToList();
            RegionDiff = onlyA.Count == 0 && onlyB.Count == 0 ? "✔ Region content is identical in PDF 1 and PDF 2"
                : string.Join(Environment.NewLine, onlyA.Select(s => "− " + s).Concat(onlyB.Select(s => "+ " + s)));
        }
        else RegionDiff = string.IsNullOrWhiteSpace(RegionTextA) ? "(No text in region – try OCR if this is a scan)" : $"{RegionTextA.Split(Environment.NewLine).Length} line(s)";
        StatusMessage = $"Read region on page {page + 1}: {RegionTextA.Length} characters";
    }

    public void AddZone(int page, PdfRect r)
    {
        if (_doc == null) return;
        string preview = ZoneService.Normalize(_doc.GetTextInRect(page, r, OcrWords(page)));
        var name = Dialogs.Prompt("New check zone", $"Zone name (e.g. Sheet, Title, Rev, Date, Scale…)\nCurrent text: {(preview.Length > 80 ? preview[..80] + "…" : preview)}", $"Zone {Zones.Count + 1}",
            suggestions: new[] { "Sheet", "Title", "Rev", "Date", "Scale", "Checked by", "Approved by" });
        if (name == null) return;
        var z = new Zone { Name = name, Page = page, Rect = r, Rule = "extract", AllPages = true };
        Zones.Add(z);
        SelectedZone = z;
        CommitNow();
        Viewer?.Refresh();
    }

    private async Task RunZonesAsync()
    {
        if (_doc == null) return;
        var doc = _doc; var docB = _docB; var zones = Zones.ToList();
        await RunJob("Checking zones…", async ct =>
        {
            var res = await Task.Run(() => ZoneService.Run(zones, doc, docB, Path.GetFileName(doc.FilePath), ct, OcrWords), ct);
            ZoneResults.Clear();
            foreach (var r in res) ZoneResults.Add(r);
            StatusMessage = $"Zone check: {res.Count(r => r.Passed)} PASS / {res.Count(r => !r.Passed)} FAIL";
        });
    }

    public void Crop(int page, PdfRect r)
    {
        if (_doc == null) return;
        int c = Dialogs.Choice("Crop", "Export the selected region as:", "PDF (vector)", "PNG 300 dpi", "Copy text");
        try
        {
            switch (c)
            {
                case 0:
                    var f = Dialogs.SaveFile(Dialogs.PdfFilter, $"{Path.GetFileNameWithoutExtension(_doc.FilePath)}_p{page + 1}_crop.pdf");
                    if (f == null) return;
                    ExportService.CropToPdf(_doc, page, r, f);
                    StatusMessage = "Cropped PDF: " + f; break;
                case 1:
                    var g = Dialogs.SaveFile("PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg", $"{Path.GetFileNameWithoutExtension(_doc.FilePath)}_p{page + 1}_crop.png");
                    if (g == null) return;
                    ExportService.SavePng(ExportService.RenderPageWithMarkups(_doc, page, 300, Markups, r), g);
                    StatusMessage = "Cropped PNG: " + g; break;
                case 2:
                    Clipboard.SetText(_doc.GetTextInRect(page, r, OcrWords(page)));
                    StatusMessage = "Region text copied."; break;
            }
        }
        catch (Exception ex) { Logger.Error("Crop", ex); Dialogs.Error("Crop failed:\n" + ex.Message); }
    }

    public void SelectText(int page, PdfRect r)
    {
        if (_doc == null) return;
        string t = _doc.GetTextInRect(page, r, OcrWords(page));
        try { if (t.Length > 0) Clipboard.SetText(t); } catch { }
        RegionTextA = t;
        StatusMessage = t.Length > 0 ? $"Selected & copied {t.Length} characters" : "No text in region";
    }

    // =================================================================== register
    private async Task DetectRegisterAsync(bool ask)
    {
        if (_doc == null) return;
        if (ask && Register.Count > 0 && !Dialogs.Confirm("Overwrite the current Drawing Register with data read from the PDF?")) return;
        var doc = _doc; var zones = Zones.ToList();
        await RunJob("Reading Drawing Register…", async ct =>
        {
            var list = await Task.Run(() =>
            {
                var r = new List<DrawingSheet>();
                for (int p = 0; p < doc.PageCount; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    r.Add(ZoneService.DetectSheet(doc, p, zones));
                    Progress = (p + 1) * 100.0 / doc.PageCount;
                }
                return r;
            }, ct);
            if (doc != _doc) return;
            Register.Clear();
            foreach (var d in list) Register.Add(d);
            ApplyRegisterToThumbnails();
            if (ask) CommitNow(); else { _lastSnapshot = Snapshot(); }
            StatusMessage = $"Drawing Register: {list.Count} sheet";
        });
    }

    private void ApplyRegisterToThumbnails()
    {
        foreach (var t in Thumbnails)
        {
            var d = Register.FirstOrDefault(r => r.Page == t.Index);
            t.Sheet = d?.Sheet ?? ""; t.Revision = string.IsNullOrEmpty(d?.Revision) ? "" : "Rev " + d!.Revision;
        }
    }

    // =================================================================== QA/QC issues
    public void CreateIssue(Markup? m, bool fromPin = false)
    {
        if (_doc == null) return;
        int page = m?.Page ?? Viewer?.CurrentPage ?? 0;
        var b = m?.Bounds ?? new PdfRect(_doc.GetPageSize(page).Width / 2, _doc.GetPageSize(page).Height / 2, 0, 0);
        var issue = new Issue
        {
            IssueId = $"ISS-{_project.NextIssueNo++:000}", Page = page, X = Math.Round(b.X + b.Width / 2, 1), Y = Math.Round(b.Y + b.Height / 2, 1),
            Sheet = Register.FirstOrDefault(r => r.Page == page)?.Sheet ?? $"P{page + 1}",
            Location = $"Page {page + 1} @ ({b.X + b.Width / 2:0}, {b.Y + b.Height / 2:0})",
            Description = m == null ? "" : (string.IsNullOrWhiteSpace(m.Text) ? m.Comment : m.Text),
            Category = m?.Type == MarkupType.Tag && m.Subject.Contains("MEP") ? "MEP" : m?.Type == MarkupType.Tag && m.Subject.Contains("ARCH") ? "Architectural" : "Structural",
            Reviewer = Author, MarkupId = m?.Id
        };
        if (fromPin && m != null) { m.Subject = issue.IssueId; }
        if (fromPin)
        {
            var d = Dialogs.Prompt(issue.IssueId, "Issue description:", "", true);
            issue.Description = d ?? "";
            if (m != null) m.Text = issue.Description;
        }
        Issues.Add(issue);
        SelectedIssue = issue;
        if (!fromPin) CommitNow();
        StatusMessage = $"Created {issue.IssueId}";
    }

    // =================================================================== compare / overlay
    public async Task OpenCompareAsync(string path)
    {
        try
        {
            var doc = await OpenDocumentAsync(path);
            if (doc == null) return;
            CloseCompare();
            DocB = doc;
            Viewer?.SetCompareDocument(doc);
            if (OverlayMode == OverlayMode.None) OverlayMode = OverlayMode.Colored;
            if (_doc != null && doc.PageCount != _doc.PageCount) StatusMessage = $"Note: page counts differ ({_doc.PageCount} vs {doc.PageCount})";
            else StatusMessage = "Opened PDF 2: " + Path.GetFileName(path);
        }
        catch (PdfOpenException ex) { Dialogs.Error(ex.Message); }
        catch (Exception ex) { Logger.Error("Open compare", ex); Dialogs.Error(ex.Message); }
    }

    private void CloseCompare()
    {
        Changes.Clear(); CurrentChange = -1;
        Viewer?.SetCompareDocument(null);
        DocB?.Dispose(); DocB = null;
        OverlayMode = OverlayMode.None;
    }

    private async Task RunCompareAsync(bool allPages)
    {
        if (_doc == null || _docB == null) return;
        var a = _doc; var b = _docB;
        int cur = Viewer?.CurrentPage ?? 0;
        await RunJob("Comparing…", async ct =>
        {
            var pages = allPages ? Enumerable.Range(0, Math.Min(a.PageCount, b.PageCount)).ToList() : new List<int> { cur };
            if (!allPages) foreach (var c in Changes.Where(c => c.Page == cur).ToList()) Changes.Remove(c);
            else Changes.Clear();
            int done = 0;
            foreach (var p in pages)
            {
                ct.ThrowIfCancellationRequested();
                int pb = Math.Min(p, b.PageCount - 1);
                var res = await Task.Run(() => CompareService.ComparePage(a, p, b, pb, ct), ct);
                foreach (var r in res) Changes.Add(r);
                Progress = ++done * 100.0 / pages.Count;
                StatusMessage = $"Compared page {p + 1}: {res.Count} change(s)";
            }
            var sorted = Changes.OrderBy(c => c.Page).ThenBy(c => c.Rect.Y).ToList();
            Changes.Clear(); foreach (var c in sorted) Changes.Add(c);
            CurrentChange = Changes.Count > 0 ? 0 : -1;
            StatusMessage = $"Compare finished: {Changes.Count} changed area(s) on {Changes.Select(c => c.Page).Distinct().Count()} page(s)";
        });
        if (CurrentChange >= 0) ShowChange();
        Viewer?.Refresh();
    }

    private void StepChange(int d)
    {
        if (Changes.Count == 0) return;
        CurrentChange = ((CurrentChange + d) % Changes.Count + Changes.Count) % Changes.Count;
        ShowChange();
    }

    private void ShowChange()
    {
        if (CurrentChange < 0 || CurrentChange >= Changes.Count) return;
        var c = Changes[CurrentChange];
        Viewer?.ZoomToRect(c.Page, c.Rect.Inflate(Math.Max(40, Math.Max(c.Rect.Width, c.Rect.Height) * 0.5)));
        StatusMessage = $"[{CurrentChange + 1}/{Changes.Count}] {c.Description}";
        OnPropertyChanged(nameof(ChangeText));
    }

    private void ExportComparePdf()
    {
        if (_docB == null) return;
        var f = Dialogs.SaveFile(Dialogs.PdfFilter + "|" + Dialogs.ExcelFilter, Path.GetFileNameWithoutExtension(_docB.FilePath) + "_compare.pdf");
        if (f == null) return;
        try
        {
            if (f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) ExportService.ExportExcel(f, ExportService.ChangesTable(Changes));
            else ExportService.ExportFlattenedPdf(_docB, f, Array.Empty<Markup>(), ScaleOf, Changes);
            StatusMessage = "Exported: " + f;
            OpenFileLocation(f);
        }
        catch (Exception ex) { Logger.Error("Export compare", ex); Dialogs.Error(ex.Message); }
    }

    // =================================================================== OCR
    private async Task OcrAsync(bool all)
    {
        if (_doc == null) return;
        var doc = _doc;
        var pages = all ? Enumerable.Range(0, doc.PageCount).ToList() : new List<int> { Viewer?.CurrentPage ?? 0 };
        await RunJob("Running OCR…", async ct =>
        {
            int done = 0, total = 0;
            foreach (var p in pages)
            {
                ct.ThrowIfCancellationRequested();
                var words = await OcrService.RecognizePageAsync(doc, p, ct);
                _project.OcrText[p] = words;
                total += words.Count;
                Progress = ++done * 100.0 / pages.Count;
                StatusMessage = $"OCR page {p + 1}: {words.Count} words";
            }
            IsDirty = true;
            StatusMessage = $"OCR finished: {total} words on {pages.Count} page(s) (Search / Read data / Zone check now available)";
        });
    }

    private void ExportSearchable()
    {
        if (_doc == null) return;
        var f = Dialogs.SaveFile(Dialogs.PdfFilter, Path.GetFileNameWithoutExtension(_doc.FilePath) + "_searchable.pdf");
        if (f == null) return;
        try { ExportService.ExportSearchablePdf(_doc, _project.OcrText, f); StatusMessage = "Exported searchable PDF: " + f; }
        catch (Exception ex) { Logger.Error("Searchable", ex); Dialogs.Error(ex.Message); }
    }

    // =================================================================== page operations
    /// <summary>"rotate:N", "delete:N", "duplicate:N", "move:FROM:TO" (0-based).</summary>
    public async Task PageOperationAsync(string op)
    {
        if (_doc == null) return;
        var parts = op.Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[1], out int page)) return;
        int n = _doc.PageCount;
        if (page < 0 || page >= n) { Dialogs.Error("Invalid page."); return; }
        var order = Enumerable.Range(0, n).Select(i => (Page: i, Rotate: 0)).ToList();
        Func<int, int?> map = i => i; // old page -> new page (null = deleted)
        Func<PdfPoint, PdfPoint>? rotateFn = null;
        switch (parts[0])
        {
            case "rotate":
                order[page] = (page, 90);
                double h = _doc.GetPageSize(page).Height;
                rotateFn = q => new PdfPoint(h - q.Y, q.X);
                break;
            case "delete":
                if (n == 1) { Dialogs.Error("Cannot delete the only page."); return; }
                if (!Dialogs.Confirm($"Delete page {page + 1}? (markups on this page are deleted too)")) return;
                order.RemoveAt(page);
                map = i => i == page ? null : i > page ? i - 1 : i;
                break;
            case "duplicate":
                order.Insert(page + 1, (page, 0));
                map = i => i > page ? i + 1 : i;
                break;
            case "move":
                if (parts.Length < 3 || !int.TryParse(parts[2], out int to)) return;
                to = Math.Clamp(to, 0, n - 1);
                if (to == page) return;
                var item = order[page]; order.RemoveAt(page); order.Insert(to, item);
                var newIndex = order.Select((o, idx) => (o.Page, idx)).ToDictionary(x => x.Page, x => x.idx);
                map = i => newIndex[i];
                break;
            default: return;
        }
        string src = _doc.FilePath;
        string work = Path.Combine(AppPaths.Root, "work");
        Directory.CreateDirectory(work);
        string outPath = Path.Combine(work, $"{Path.GetFileNameWithoutExtension(_project.PdfPath)}_{DateTime.Now:HHmmssfff}.pdf");
        await RunJob("Processing pages…", async ct =>
        {
            await Task.Run(() => ExportService.RebuildPdf(src, order, outPath), ct);
            var newDoc = await Task.Run(() => PdfDocument.Open(outPath), ct);
            // remap markups / issues / zones / register
            Commit();
            foreach (var m in Markups.ToList())
            {
                var np = map(m.Page);
                if (np == null) { Markups.Remove(m); continue; }
                if (rotateFn != null && m.Page == page)
                {
                    m.Points = m.Points.Select(rotateFn).ToList();
                    if (m.Rects != null) m.Rects = m.Rects.Select(r => PdfRect.FromPoints(rotateFn(new PdfPoint(r.X, r.Y)), rotateFn(new PdfPoint(r.Right, r.Bottom)))).ToList();
                }
                m.Page = np.Value;
            }
            foreach (var i in Issues) i.Page = map(i.Page) ?? i.Page;
            foreach (var z in Zones) z.Page = map(z.Page) ?? 0;
            foreach (var d in Register.ToList()) { var np = map(d.Page); if (np == null) Register.Remove(d); else d.Page = np.Value; }
            if (parts[0] == "duplicate") { var orig = Register.FirstOrDefault(r => r.Page == page); Register.Add(new DrawingSheet { Page = page + 1, Sheet = (orig?.Sheet ?? "") + "-copy", Title = orig?.Title ?? "", Revision = orig?.Revision ?? "" }); }
            _project.PageScales = _project.PageScales.Where(kv => map(kv.Key) != null).ToDictionary(kv => map(kv.Key)!.Value, kv => kv.Value);
            _project.OcrText = _project.OcrText.Where(kv => map(kv.Key) != null && !(parts[0] == "rotate" && kv.Key == page)).ToDictionary(kv => map(kv.Key)!.Value, kv => kv.Value);
            var old = _doc;
            _docCts.Cancel(); _docCts = new CancellationTokenSource();
            Doc = newDoc;
            BuildThumbnails();
            ApplyRegisterToThumbnails();
            Viewer?.ShowDocument(newDoc);
            Viewer?.GoToPage(Math.Clamp(map(page) ?? page, 0, newDoc.PageCount - 1));
            old?.Dispose();
            _pdfModified = true;
            CommitNow();
            StatusMessage = $"Page {page + 1}: {parts[0]} done. Use File > Save PDF As… to save the PDF.";
        });
    }

    // =================================================================== exports
    private void ExportTable(TableData t, bool excel) => ExportTables(excel, t);

    private void ExportTables(bool excel, params TableData[] tables)
    {
        string baseName = (_project.Name.Length > 0 ? _project.Name : "JNN") + "_" + tables[0].Name;
        var f = Dialogs.SaveFile(excel ? Dialogs.ExcelFilter : Dialogs.CsvFilter, baseName + (excel ? ".xlsx" : ".csv"));
        if (f == null) return;
        try
        {
            if (excel) ExportService.ExportExcel(f, tables);
            else
            {
                ExportService.ExportCsv(f, tables[0]);
                for (int i = 1; i < tables.Length; i++) ExportService.ExportCsv(Path.ChangeExtension(f, null) + "_" + tables[i].Name + ".csv", tables[i]);
            }
            StatusMessage = "Exported: " + f;
            OpenFileLocation(f);
        }
        catch (IOException ex) { Dialogs.Error("Cannot write the file (is it open in Excel?):\n" + ex.Message); }
        catch (Exception ex) { Logger.Error("Export", ex); Dialogs.Error(ex.Message); }
    }

    private void ExportFlattenedPdf()
    {
        if (_doc == null) return;
        var f = Dialogs.SaveFile(Dialogs.PdfFilter, Path.GetFileNameWithoutExtension(_doc.FilePath) + "_markup.pdf", "Export PDF with markups");
        if (f == null) return;
        try
        {
            if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(_doc.FilePath), StringComparison.OrdinalIgnoreCase)) { Dialogs.Error("Choose a name different from the open file."); return; }
            ExportService.ExportFlattenedPdf(_doc, f, Markups, ScaleOf);
            StatusMessage = "Exported PDF: " + f;
            OpenFileLocation(f);
        }
        catch (Exception ex) { Logger.Error("Export PDF", ex); Dialogs.Error("PDF export failed:\n" + ex.Message); }
    }

    private void ExportPng()
    {
        if (_doc == null) return;
        int page = Viewer?.CurrentPage ?? 0;
        var f = Dialogs.SaveFile("PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg", $"{Path.GetFileNameWithoutExtension(_doc.FilePath)}_p{page + 1}.png");
        if (f == null) return;
        try
        {
            var size = _doc.GetPageSize(page);
            double dpi = Math.Min(200, 9000 / Math.Max(size.Width, size.Height) * 72);
            ExportService.SavePng(ExportService.RenderPageWithMarkups(_doc, page, dpi, Markups), f);
            StatusMessage = "Exported image: " + f;
        }
        catch (Exception ex) { Logger.Error("PNG", ex); Dialogs.Error(ex.Message); }
    }

    private void ExportReport()
    {
        if (_doc == null) return;
        RefreshTakeoff();
        int open = Markups.Count(m => m.Status is "Open" or "In Progress");
        string summary = $"{Markups.Count} markups ({open} open), {Issues.Count} issues " +
            $"({Issues.Count(i => i.Severity is "High" or "Critical")} high/critical, {Issues.Count(i => i.Status == "APPROVED")} approved), " +
            $"{Markups.Count(m => m.IsMeasurement)} measurements, {Takeoff.Count} takeoff items, {Changes.Count} revision changes.";
        var reg = Register.FirstOrDefault();
        var info = Dialogs.ReportDialog(new ExportService.ReportInfo(_project.Name, Path.GetFileName(_doc.FilePath), reg?.Revision ?? "", Author, summary));
        if (info == null) return;
        var f = Dialogs.SaveFile("PDF report (*.pdf)|*.pdf|Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", (_project.Name.Length > 0 ? _project.Name : "JNN") + "_Report.pdf");
        if (f == null) return;
        var summaryTable = new TableData("Summary", new[] { "Item", "Value" }, new List<object?[]>
        {
            new object?[] { "Project", info.Project }, new object?[] { "Drawing", info.Drawing }, new object?[] { "Revision", info.Revision },
            new object?[] { "Reviewer", info.Reviewer }, new object?[] { "Date", DateTime.Now.ToString("dd/MM/yyyy HH:mm") }, new object?[] { "Summary", info.Summary }
        });
        var tables = new[]
        {
            ExportService.IssuesTable(Issues), ExportService.MeasurementsTable(Markups), ExportService.TakeoffTable(Takeoff),
            ExportService.MarkupsTable(Markups), ExportService.RegisterTable(Register)
        };
        try
        {
            if (f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) ExportService.ExportReportPdf(f, info, tables);
            else if (f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) ExportService.ExportExcel(f, new[] { summaryTable }.Concat(tables).ToArray());
            else
            {
                ExportService.ExportCsv(f, summaryTable);
                foreach (var t in tables) ExportService.ExportCsv(Path.ChangeExtension(f, null) + "_" + t.Name + ".csv", t);
            }
            StatusMessage = "Exported report: " + f;
            OpenFileLocation(f);
        }
        catch (Exception ex) { Logger.Error("Report", ex); Dialogs.Error("Report export failed:\n" + ex.Message); }
    }

    private static void OpenFileLocation(string f)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{f}\"") { UseShellExecute = true }); } catch { }
    }
}
