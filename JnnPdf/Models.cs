// ============================================================================
//  Models.cs  –  DATA MODEL (pure data, no UI / no PDF engine code)
//  Document, Page, Markup, Measurement, Takeoff, Drawing, Revision, Issue,
//  User, Stamp, Hyperlink, Zone, Project ...
//  All markup geometry is stored in PAGE DISPLAY COORDINATES (PDF points,
//  origin = top-left of the page as displayed, /Rotate already applied).
// ============================================================================
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace JnnPdf;

/// <summary>Base class implementing INotifyPropertyChanged (MVVM).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Point in page display coordinates (PDF points, 1pt = 1/72 inch).</summary>
public record struct PdfPoint(double X, double Y);

/// <summary>Rectangle in page display coordinates.</summary>
public record struct PdfRect(double X, double Y, double Width, double Height)
{
    [JsonIgnore] public double Right => X + Width;
    [JsonIgnore] public double Bottom => Y + Height;
    public bool Contains(double px, double py) => px >= X && px <= Right && py >= Y && py <= Bottom;
    public bool IntersectsWith(PdfRect o) => o.X < Right && o.Right > X && o.Y < Bottom && o.Bottom > Y;
    public static PdfRect FromPoints(PdfPoint a, PdfPoint b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    public PdfRect Inflate(double d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
    public PdfRect Union(PdfRect o)
    {
        double x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
        return new PdfRect(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }
}

// ----------------------------------------------------------------------------
//  Enumerations
// ----------------------------------------------------------------------------
public enum MarkupType
{
    // Text
    TextBox, Callout, Typewriter, Label,
    // Shapes
    Line, Arrow, Polyline, Rectangle, Ellipse, Cloud, Polygon,
    // Drawing
    Pen, Highlighter, Highlight,
    // Construction
    Tag, Stamp, IssuePin, Hyperlink,
    // Measurement
    Length, PolyLength, Area, Perimeter, Count, Angle
}

/// <summary>Active interaction tool on the PDF canvas.</summary>
public enum ToolKind
{
    Select, Pan, ZoomBox, TextSelect,
    TextBox, Callout, Typewriter, Label,
    Line, Arrow, Polyline, Rectangle, Ellipse, Cloud, Polygon,
    Pen, Highlighter, Highlight,
    Tag, Stamp, IssuePin, Hyperlink,
    Length, PolyLength, Area, Perimeter, Count, Angle,
    Calibrate, RegionRead, Zone, Crop
}

public static class Lists
{
    public static readonly string[] MarkupStatuses = { "Open", "In Progress", "Resolved", "Closed", "Rejected" };
    public static readonly string[] IssueStatuses = { "NOT CHECKED", "CHECKING", "COMMENTED", "RESUBMITTED", "VERIFIED", "APPROVED" };
    public static readonly string[] Severities = { "Low", "Medium", "High", "Critical" };
    public static readonly string[] IssueCategories = { "Structural", "Architectural", "MEP", "Civil", "Coordination", "Documentation", "Other" };
    public static readonly string[] Tags =
    {
        "RFI", "TBC", "TB", "QA", "QC", "REV", "CHECK", "APPROVED", "REJECTED", "CLASH",
        "MISSING INFO", "WRONG DIMENSION", "WRONG LEVEL", "WRONG TAG", "MISSING REBAR",
        "WRONG REBAR", "STRUCTURAL ISSUE", "ARCH ISSUE", "MEP ISSUE"
    };
    public static readonly string[] Stamps =
    {
        "APPROVED", "APPROVED AS NOTED", "REVIEWED", "REJECTED", "FOR CONSTRUCTION",
        "FOR INFORMATION", "PRELIMINARY", "SUPERSEDED"
    };
    public static readonly string[] ZoneRules = { "compare", "extract", "contains", "equals", "regex", "not_empty", "number_range" };
    public static readonly string[] TakeoffCategories = { "Concrete", "Rebar", "Formwork", "Masonry", "Finishes", "Doors", "Windows", "MEP", "Columns", "Openings", "Other" };
    public static readonly string[] DrawingStatuses = { "Current", "Superseded", "For Review", "Void" };
    public static readonly string[] PaperUnits = { "mm", "in", "pt" };
    public static readonly string[] RealUnits = { "m", "cm", "mm", "ft", "in" };
}

// ----------------------------------------------------------------------------
//  Markup (also used for Measurement, Takeoff source, Stamp, Hyperlink, Tag)
// ----------------------------------------------------------------------------
public sealed class Markup : ObservableObject
{
    private int _id;
    private MarkupType _type;
    private int _page;
    private List<PdfPoint> _points = new();
    private List<PdfRect>? _rects;
    private string _color = "#E53935";
    private string? _fillColor;
    private double _lineWidth = 2;
    private double _opacity = 1;
    private double _fontSize = 12;
    private string _text = "";
    private string _author = Environment.UserName;
    private DateTime _createdDate = DateTime.Now;
    private DateTime _modifiedDate = DateTime.Now;
    private string _status = "Open";
    private string _subject = "";
    private string _comment = "";
    private string _category = "";
    private string _takeoffName = "";
    private double _depth;
    private string _linkTarget = "";
    private string _revision = "";
    private string _valueText = "";
    private double _value;

    public int Id { get => _id; set => Set(ref _id, value); }
    public MarkupType Type { get => _type; set { if (Set(ref _type, value)) OnPropertyChanged(nameof(IsMeasurement)); } }
    /// <summary>Zero-based page index.</summary>
    public int Page { get => _page; set { if (Set(ref _page, value)) OnPropertyChanged(nameof(PageNumber)); } }
    /// <summary>Geometry. Meaning depends on Type (2 pts for box/line, N pts for poly, 3 pts for angle...).</summary>
    public List<PdfPoint> Points { get => _points; set { _points = value ?? new(); OnGeometryChanged(); } }
    /// <summary>Optional list of rectangles (text highlight snapped to words).</summary>
    public List<PdfRect>? Rects { get => _rects; set { _rects = value; OnGeometryChanged(); } }
    public string Color { get => _color; set => Touch(ref _color, value); }
    public string? FillColor { get => _fillColor; set => Touch(ref _fillColor, value); }
    public double LineWidth { get => _lineWidth; set => Touch(ref _lineWidth, Math.Clamp(value, 0.1, 50)); }
    public double Opacity { get => _opacity; set => Touch(ref _opacity, Math.Clamp(value, 0.05, 1)); }
    public double FontSize { get => _fontSize; set => Touch(ref _fontSize, Math.Clamp(value, 4, 200)); }
    public string Text { get => _text; set => Touch(ref _text, value ?? ""); }
    public string Author { get => _author; set => Touch(ref _author, value ?? ""); }
    public DateTime CreatedDate { get => _createdDate; set => Set(ref _createdDate, value); }
    public DateTime ModifiedDate { get => _modifiedDate; set => Set(ref _modifiedDate, value); }
    public string Status { get => _status; set => Touch(ref _status, value ?? "Open"); }
    public string Subject { get => _subject; set => Touch(ref _subject, value ?? ""); }
    public string Comment { get => _comment; set => Touch(ref _comment, value ?? ""); }
    /// <summary>Takeoff category (Concrete, Rebar...). Empty = not a takeoff item.</summary>
    public string Category { get => _category; set => Touch(ref _category, value ?? ""); }
    /// <summary>Takeoff item name (e.g. "Slab L1").</summary>
    public string TakeoffName { get => _takeoffName; set => Touch(ref _takeoffName, value ?? ""); }
    /// <summary>Depth/height (real units) – Area x Depth = Volume for takeoff.</summary>
    public double Depth { get => _depth; set => Touch(ref _depth, Math.Max(0, value)); }
    /// <summary>Hyperlink target: "page:25", "https://...", or a file path.</summary>
    public string LinkTarget { get => _linkTarget; set => Touch(ref _linkTarget, value ?? ""); }
    /// <summary>Revision label (used by Stamps).</summary>
    public string Revision { get => _revision; set => Touch(ref _revision, value ?? ""); }

    // ---- Computed (not serialized) ----
    [JsonIgnore] public int PageNumber => Page + 1;
    [JsonIgnore] public bool IsMeasurement => Type is MarkupType.Length or MarkupType.PolyLength or MarkupType.Area
        or MarkupType.Perimeter or MarkupType.Count or MarkupType.Angle;
    /// <summary>Measured value in real units (set by MeasurementService).</summary>
    [JsonIgnore] public double Value { get => _value; set => Set(ref _value, value); }
    /// <summary>Formatted measurement, e.g. "Length = 4.25 m".</summary>
    [JsonIgnore] public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
    [JsonIgnore] public PdfRect Bounds => Geometry.BoundsOf(this);
    [JsonIgnore] public double X => Math.Round(Bounds.X, 1);
    [JsonIgnore] public double Y => Math.Round(Bounds.Y, 1);
    [JsonIgnore] public double Width => Math.Round(Bounds.Width, 1);
    [JsonIgnore] public double Height => Math.Round(Bounds.Height, 1);

    /// <summary>Call after mutating Points in place.</summary>
    public void OnGeometryChanged()
    {
        ModifiedDate = DateTime.Now;
        OnPropertyChanged(nameof(Points));
        OnPropertyChanged(nameof(Bounds));
        OnPropertyChanged(nameof(X)); OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width)); OnPropertyChanged(nameof(Height));
    }

    private void Touch<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name)) ModifiedDate = DateTime.Now;
    }
}

// ----------------------------------------------------------------------------
//  QA/QC Issue
// ----------------------------------------------------------------------------
public sealed class Issue : ObservableObject
{
    private string _issueId = "";
    private string _sheet = "";
    private int _page;
    private string _location = "";
    private string _category = "Structural";
    private string _description = "";
    private string _severity = "Medium";
    private string _assignedTo = "";
    private string _status = "NOT CHECKED";
    private DateTime? _dueDate = DateTime.Today.AddDays(7);
    private string _reviewer = Environment.UserName;
    private int? _markupId;
    private double _x, _y;

    public string IssueId { get => _issueId; set => Set(ref _issueId, value); }
    public string Sheet { get => _sheet; set => Set(ref _sheet, value); }
    public int Page { get => _page; set { if (Set(ref _page, value)) OnPropertyChanged(nameof(PageNumber)); } }
    [JsonIgnore] public int PageNumber => Page + 1;
    public double X { get => _x; set => Set(ref _x, value); }
    public double Y { get => _y; set => Set(ref _y, value); }
    public string Location { get => _location; set => Set(ref _location, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Severity { get => _severity; set => Set(ref _severity, value); }
    public string AssignedTo { get => _assignedTo; set => Set(ref _assignedTo, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public DateTime? DueDate { get => _dueDate; set => Set(ref _dueDate, value); }
    public string Reviewer { get => _reviewer; set => Set(ref _reviewer, value); }
    public int? MarkupId { get => _markupId; set => Set(ref _markupId, value); }
    public DateTime Created { get; set; } = DateTime.Now;
}

// ----------------------------------------------------------------------------
//  Check zone (region QA) – kept from the Python prototype
// ----------------------------------------------------------------------------
public sealed class Zone : ObservableObject
{
    private string _name = "Zone";
    private int _page;
    private PdfRect _rect;
    private string _rule = "extract";
    private string _expected = "";
    private bool _allPages = true;

    public string Name { get => _name; set => Set(ref _name, value); }
    public int Page { get => _page; set => Set(ref _page, value); }
    public PdfRect Rect { get => _rect; set => Set(ref _rect, value); }
    /// <summary>compare | extract | contains | equals | regex | not_empty | number_range</summary>
    public string Rule { get => _rule; set => Set(ref _rule, value); }
    /// <summary>Expected value / pattern / "min..max" for number_range.</summary>
    public string Expected { get => _expected; set => Set(ref _expected, value); }
    /// <summary>Apply the same rectangle to every page (title block fields...).</summary>
    public bool AllPages { get => _allPages; set => Set(ref _allPages, value); }
}

/// <summary>Result row of a zone check.</summary>
public sealed record ZoneResult(string File, int Page, string Zone, string Rule, string Value, string ValueB, bool Passed, string Message)
{
    public int PageNumber => Page + 1;
    public string Result => Passed ? "PASS" : "FAIL";
}

// ----------------------------------------------------------------------------
//  Drawing register (Drawing + Revision)
// ----------------------------------------------------------------------------
public sealed class DrawingSheet : ObservableObject
{
    private int _page;
    private string _sheet = "", _title = "", _revision = "", _date = "", _status = "Current";
    public int Page { get => _page; set { if (Set(ref _page, value)) OnPropertyChanged(nameof(PageNumber)); } }
    [JsonIgnore] public int PageNumber => Page + 1;
    public string Sheet { get => _sheet; set => Set(ref _sheet, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Revision { get => _revision; set => Set(ref _revision, value); }
    public string Date { get => _date; set => Set(ref _date, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
}

/// <summary>Aggregated takeoff line (computed from measurement markups that have a Category).</summary>
public sealed record TakeoffSummary(string Category, string Name, string Type, double Quantity, string Unit, int Items, string Pages, string Color);

/// <summary>A changed region found by CompareService.</summary>
public sealed record ChangeRegion(int Page, PdfRect Rect, string Kind, string Description)
{
    public int PageNumber => Page + 1;
}

/// <summary>Text search hit.</summary>
public sealed record SearchHit(int Page, PdfRect Rect, string Context)
{
    public int PageNumber => Page + 1;
    public string Display => $"Page {Page + 1}:  {Context}";
}

/// <summary>Bookmark (outline) entry.</summary>
public sealed class BookmarkItem
{
    public string Title { get; set; } = "";
    public int Page { get; set; } = -1;
    public List<BookmarkItem> Children { get; } = new();
}

/// <summary>Scale: PaperValue [PaperUnit] on PDF = RealValue [RealUnit] in reality.</summary>
public sealed class ScaleSetting
{
    public double PaperValue { get; set; } = 1;
    public string PaperUnit { get; set; } = "mm";
    public double RealValue { get; set; } = 100;
    public string RealUnit { get; set; } = "mm";
    /// <summary>Unit used to display results.</summary>
    public string DisplayUnit { get; set; } = "m";

    public static ScaleSetting Ratio(double denominator)
        => new() { PaperValue = 1, PaperUnit = "mm", RealValue = denominator, RealUnit = "mm", DisplayUnit = "m" };

    [JsonIgnore]
    public string Label
    {
        get
        {
            if (PaperUnit == RealUnit && Math.Abs(PaperValue - 1) < 1e-9)
                return $"1:{RealValue:0.##}";
            return $"{PaperValue:0.###}{PaperUnit} = {RealValue:0.###}{RealUnit}";
        }
    }
}

/// <summary>Information about an opened PDF (Document + Page models).</summary>
public sealed class PdfDocumentInfo
{
    public string Path { get; init; } = "";
    public int PageCount { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
    public long FileSize { get; init; }
}

/// <summary>Per-page information.</summary>
public sealed record PdfPageInfo(int Index, double Width, double Height, int Rotation);

/// <summary>Current user (stored in settings DB).</summary>
public sealed class UserInfo
{
    public string Name { get; set; } = Environment.UserName;
    public string Company { get; set; } = "";
}

// ----------------------------------------------------------------------------
//  Project file (*.jnnreview, JSON)
// ----------------------------------------------------------------------------
public sealed class ProjectData
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled";
    /// <summary>PDF path relative to the project file (if possible).</summary>
    public string PdfRelativePath { get; set; } = "";
    /// <summary>Absolute PDF path (fallback).</summary>
    public string PdfPath { get; set; } = "";
    /// <summary>Revision B / overlay document (optional).</summary>
    public string ComparePdfPath { get; set; } = "";
    public UserInfo User { get; set; } = new();
    public List<Markup> Markups { get; set; } = new();
    public List<Issue> Issues { get; set; } = new();
    public List<Zone> Zones { get; set; } = new();
    public List<DrawingSheet> Register { get; set; } = new();
    public ScaleSetting DefaultScale { get; set; } = ScaleSetting.Ratio(100);
    /// <summary>Page specific scales (page index -> scale).</summary>
    public Dictionary<int, ScaleSetting> PageScales { get; set; } = new();
    /// <summary>OCR text layers (page index -> words) for scanned pages.</summary>
    public Dictionary<int, List<OcrWord>> OcrText { get; set; } = new();
    public int NextMarkupId { get; set; } = 1;
    public int NextIssueNo { get; set; } = 1;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
}

/// <summary>Word recognised by OCR (display coordinates).</summary>
public sealed record OcrWord(string Text, PdfRect Rect);

/// <summary>Single character (glyph) with its box in display coordinates.</summary>
public readonly record struct TextGlyph(string Text, PdfRect Rect);

/// <summary>Word extracted from the PDF text layer (display coordinates).
/// <paramref name="Glyphs"/> holds per-character boxes when known (PDF text layer), null for OCR words.</summary>
public sealed record TextWord(string Text, PdfRect Rect, IReadOnlyList<TextGlyph>? Glyphs = null);
