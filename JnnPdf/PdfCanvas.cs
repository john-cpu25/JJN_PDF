// ============================================================================
//  PdfCanvas.cs  –  PDF VIEW + MARKUP ENGINE (custom WPF FrameworkElement)
//  * Page rendering: low-res base bitmap + hi-res tile of the visible area
//    (async, cancellable, debounced) – never renders the whole document.
//  * Zoom / pan / rotate view, fit page / width, zoom to rectangle.
//  * Overlay of a second PDF (A only / B only / A+B colour / transparency / blink)
//    with move / rotate / scale alignment.
//  * Markup tools (text, shapes, pen, highlight, tags, stamps, hyperlinks),
//    measurement tools (length, polylength, area, perimeter, count, angle),
//    calibrate, region select (read data / zone / crop / text select).
//  * Selection, move, resize (handles), hit-testing.
//  MarkupRenderer draws markups and is reused by PNG export and printing.
// ============================================================================
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JnnPdf;

public enum OverlayMode { None, AOnly, BOnly, Colored, Transparency, Blink }

/// <summary>Draws markups in page coordinates (DrawingContext already transformed).</summary>
public static class MarkupRenderer
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static SolidColorBrush Brush(string hex, double opacity = 1)
    {
        string key = hex + "|" + opacity.ToString("0.###", CultureInfo.InvariantCulture);
        if (Brushes.TryGetValue(key, out var b)) return b;
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { c = Colors.Red; }
        b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        if (Brushes.Count > 500) Brushes.Clear();
        Brushes[key] = b;
        return b;
    }

    public static Pen MakePen(string hex, double width, double opacity = 1, DashStyle? dash = null)
    {
        var p = new Pen(Brush(hex, opacity), width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dash != null) p.DashStyle = dash;
        p.Freeze();
        return p;
    }

    public static StreamGeometry Poly(IReadOnlyList<PdfPoint> pts, bool closed, bool filled)
    {
        var g = new StreamGeometry();
        if (pts.Count == 0) { g.Freeze(); return g; }
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), filled, closed);
            if (pts.Count > 1) ctx.PolyLineTo(pts.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), true, true);
        }
        g.Freeze();
        return g;
    }

    public static FormattedText Text(string s, double size, Brush brush, bool bold = false, double maxWidth = 0)
    {
        var ft = new FormattedText(s ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, Math.Max(1, size), brush, 1.0);
        if (maxWidth > 1) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    private static Rect R(PdfRect r) => new(r.X, r.Y, Math.Max(0.01, r.Width), Math.Max(0.01, r.Height));
    private static Point P(PdfPoint p) => new(p.X, p.Y);

    /// <summary>Draws a markup. <paramref name="zoom"/> = screen pixels per point (keeps hairlines visible).</summary>
    public static void Draw(DrawingContext dc, Markup m, double zoom, bool preview)
    {
        var p = m.Points;
        double lw = Math.Max(m.LineWidth, 0.9 / Math.Max(zoom, 0.01));
        var pen = MakePen(m.Color, lw, m.Opacity);
        Brush? fill = m.FillColor != null ? Brush(m.FillColor, Math.Min(0.35, m.Opacity)) : null;
        switch (m.Type)
        {
            case MarkupType.Line:
            case MarkupType.Polyline:
            case MarkupType.Pen:
            case MarkupType.Length:
            case MarkupType.PolyLength:
            case MarkupType.Angle:
                if (p.Count >= 2) dc.DrawGeometry(null, pen, Poly(p, false, false));
                if (m.Type == MarkupType.Length && p.Count >= 2) { DrawTick(dc, p[0], p[1], pen, 6 / zoom + 3); DrawTick(dc, p[1], p[0], pen, 6 / zoom + 3); }
                if (m.Type is MarkupType.PolyLength or MarkupType.Angle) foreach (var q in p) dc.DrawEllipse(pen.Brush, null, P(q), 2.5 / zoom + 0.5, 2.5 / zoom + 0.5);
                break;
            case MarkupType.Highlighter:
                if (p.Count >= 2) dc.DrawGeometry(null, MakePen(m.Color, Math.Max(m.LineWidth, 8), 0.4), Poly(p, false, false));
                break;
            case MarkupType.Arrow:
                if (p.Count >= 2)
                {
                    dc.DrawGeometry(null, pen, Poly(p, false, false));
                    dc.DrawGeometry(pen.Brush, pen, Poly(ExportService.ArrowHead(p[^2], p[^1], Math.Max(8, lw * 4)), true, true));
                }
                break;
            case MarkupType.Polygon:
            case MarkupType.Perimeter:
                if (p.Count >= 2) dc.DrawGeometry(fill, pen, Poly(p, !preview, fill != null));
                break;
            case MarkupType.Area:
                if (p.Count >= 2) dc.DrawGeometry(Brush(m.FillColor ?? m.Color, 0.18), pen, Poly(p, true, true));
                break;
            case MarkupType.Rectangle:
                if (p.Count >= 2) dc.DrawRectangle(fill, pen, R(PdfRect.FromPoints(p[0], p[1])));
                break;
            case MarkupType.Hyperlink:
                if (p.Count >= 2)
                {
                    var hr = R(PdfRect.FromPoints(p[0], p[1]));
                    dc.DrawRectangle(Brush("#1E88E5", 0.08), MakePen("#1E88E5", lw, 0.9, DashStyles.Dash), hr);
                    var ft = Text("🔗 " + m.LinkTarget, Math.Max(7, m.FontSize * 0.7), Brush("#1E88E5"));
                    dc.DrawText(ft, new Point(hr.X, hr.Bottom + 1));
                }
                break;
            case MarkupType.Ellipse:
                if (p.Count >= 2)
                {
                    var er = R(PdfRect.FromPoints(p[0], p[1]));
                    dc.DrawEllipse(fill, pen, new Point(er.X + er.Width / 2, er.Y + er.Height / 2), er.Width / 2, er.Height / 2);
                }
                break;
            case MarkupType.Cloud:
            case MarkupType.Tag:
                if (p.Count >= 2)
                {
                    var cr = PdfRect.FromPoints(p[0], p[1]);
                    double arc = Math.Clamp(Math.Min(cr.Width, cr.Height) / 6, 6, 30);
                    dc.DrawGeometry(fill, pen, Poly(Geometry.CloudPoints(Geometry.RectCorners(cr), arc), true, fill != null));
                    if (m.Type == MarkupType.Tag)
                    {
                        string tag = string.IsNullOrEmpty(m.Subject) ? "TAG" : m.Subject;
                        var ft = Text(tag, m.FontSize, System.Windows.Media.Brushes.White, true);
                        var tr = new Rect(cr.X, cr.Y - ft.Height - 8, ft.Width + 10, ft.Height + 6);
                        dc.DrawRoundedRectangle(Brush(m.Color), null, tr, 3, 3);
                        dc.DrawText(ft, new Point(tr.X + 5, tr.Y + 3));
                        if (!string.IsNullOrWhiteSpace(m.Text))
                            dc.DrawText(Text(m.Text, m.FontSize, Brush(m.Color), false, Math.Max(80, cr.Width)), new Point(tr.Right + 5, tr.Y + 3));
                    }
                }
                break;
            case MarkupType.TextBox:
            case MarkupType.Label:
                if (p.Count >= 2)
                {
                    var tr = R(PdfRect.FromPoints(p[0], p[1]));
                    dc.DrawRectangle(m.Type == MarkupType.Label ? System.Windows.Media.Brushes.White : (fill ?? Brush("#FFFFFF", 0.85)), pen, tr);
                    var ft = Text(m.Text, m.FontSize, Brush(m.Color), m.Type == MarkupType.Label, Math.Max(5, tr.Width - 6));
                    ft.MaxTextHeight = Math.Max(m.FontSize * 1.3, tr.Height - 4);
                    dc.DrawText(ft, new Point(tr.X + 3, tr.Y + 2));
                }
                break;
            case MarkupType.Typewriter:
                if (p.Count >= 1) dc.DrawText(Text(string.IsNullOrEmpty(m.Text) ? "Text" : m.Text, m.FontSize, Brush(m.Color)), P(p[0]));
                break;
            case MarkupType.Callout:
                if (p.Count >= 3)
                {
                    var box = R(PdfRect.FromPoints(p[1], p[2]));
                    var c = new PdfPoint(box.X + box.Width / 2, box.Y + box.Height / 2);
                    var edge = new PdfPoint(Math.Clamp(p[0].X, box.Left, box.Right), Math.Clamp(p[0].Y, box.Top, box.Bottom));
                    if (box.Contains(P(p[0]))) edge = c;
                    dc.DrawLine(pen, P(p[0]), P(edge));
                    dc.DrawGeometry(pen.Brush, pen, Poly(ExportService.ArrowHead(edge, p[0], Math.Max(8, lw * 4)), true, true));
                    dc.DrawRectangle(System.Windows.Media.Brushes.White, pen, box);
                    var ft = Text(m.Text, m.FontSize, Brush(m.Color), false, Math.Max(5, box.Width - 6));
                    ft.MaxTextHeight = Math.Max(m.FontSize * 1.3, box.Height - 4);
                    dc.DrawText(ft, new Point(box.X + 3, box.Y + 2));
                }
                else if (p.Count == 2) dc.DrawLine(pen, P(p[0]), P(p[1]));
                break;
            case MarkupType.Highlight:
                {
                    var b = Brush(m.Color, 0.35);
                    if (m.Rects != null && m.Rects.Count > 0) foreach (var r in m.Rects) dc.DrawRectangle(b, null, R(r));
                    else if (p.Count >= 2) dc.DrawRectangle(b, null, R(PdfRect.FromPoints(p[0], p[1])));
                }
                break;
            case MarkupType.Stamp:
                if (p.Count >= 2)
                {
                    var sr = R(PdfRect.FromPoints(p[0], p[1]));
                    var sp = MakePen(m.Color, Math.Max(2, m.LineWidth), 0.9);
                    dc.DrawRoundedRectangle(Brush("#FFFFFF", 0.6), sp, sr, 6, 6);
                    var inner = sr; inner.Inflate(-3, -3);
                    if (inner.Width > 0 && inner.Height > 0) dc.DrawRoundedRectangle(null, MakePen(m.Color, 0.8, 0.9), inner, 4, 4);
                    double fs = Math.Clamp(sr.Height * 0.32, 4, 80);
                    var t1 = Text(m.Subject, fs, Brush(m.Color, 0.95), true, Math.Max(5, sr.Width - 12));
                    dc.DrawText(t1, new Point(sr.X + 8, sr.Y + 5));
                    var t2 = Text($"{m.CreatedDate:dd/MM/yyyy}  •  {m.Author}{(string.IsNullOrEmpty(m.Revision) ? "" : "  •  Rev " + m.Revision)}", fs * 0.42, Brush(m.Color, 0.95), false, Math.Max(5, sr.Width - 12));
                    dc.DrawText(t2, new Point(sr.X + 8, sr.Bottom - t2.Height - 5));
                }
                break;
            case MarkupType.IssuePin:
                if (p.Count >= 1)
                {
                    double r = 9;
                    dc.DrawEllipse(Brush(m.Color), MakePen("#FFFFFF", 1.5), P(p[0]), r, r);
                    var ft = Text("!", 12, System.Windows.Media.Brushes.White, true);
                    dc.DrawText(ft, new Point(p[0].X - ft.Width / 2, p[0].Y - ft.Height / 2));
                    var lbl = Text(m.Subject, 9, Brush(m.Color), true);
                    dc.DrawRoundedRectangle(Brush("#FFFFFF", 0.85), null, new Rect(p[0].X + r + 2, p[0].Y - lbl.Height / 2 - 1, lbl.Width + 6, lbl.Height + 2), 2, 2);
                    dc.DrawText(lbl, new Point(p[0].X + r + 5, p[0].Y - lbl.Height / 2));
                }
                break;
            case MarkupType.Count:
                for (int i = 0; i < p.Count; i++)
                {
                    double r = Math.Max(4, 5 / Math.Max(zoom, 0.2));
                    dc.DrawEllipse(Brush(m.Color, 0.85), MakePen("#FFFFFF", r * 0.25), P(p[i]), r, r);
                    dc.DrawText(Text((i + 1).ToString(), r * 1.4, Brush(m.Color), true), new Point(p[i].X + r, p[i].Y - r * 2.2));
                }
                break;
        }

        // Measurement label
        if (m.IsMeasurement && p.Count > 0 && !string.IsNullOrEmpty(m.ValueText))
        {
            double fs = Math.Max(m.FontSize, 10 / Math.Max(zoom, 0.05));
            var ft = Text(m.ValueText, fs, Brush(m.Color), true);
            Point at;
            if (m.Type is MarkupType.Area or MarkupType.Perimeter)
            {
                var b = m.Bounds; at = new Point(b.X + b.Width / 2 - ft.Width / 2, b.Y + b.Height / 2 - ft.Height / 2);
            }
            else if (m.Type == MarkupType.Length && p.Count >= 2)
                at = new Point((p[0].X + p[1].X) / 2 - ft.Width / 2, (p[0].Y + p[1].Y) / 2 - ft.Height - 2);
            else at = new Point(p[^1].X + 8 / zoom, p[^1].Y - ft.Height - 4 / zoom);
            dc.DrawRoundedRectangle(Brush("#FFFFFF", 0.85), null, new Rect(at.X - 2, at.Y - 1, ft.Width + 4, ft.Height + 2), 2, 2);
            dc.DrawText(ft, at);
        }
    }

    private static void DrawTick(DrawingContext dc, PdfPoint a, PdfPoint b, Pen pen, double size)
    {
        double ang = Math.Atan2(b.Y - a.Y, b.X - a.X) + Math.PI / 2;
        dc.DrawLine(pen, new Point(a.X + Math.Cos(ang) * size, a.Y + Math.Sin(ang) * size), new Point(a.X - Math.Cos(ang) * size, a.Y - Math.Sin(ang) * size));
    }
}

/// <summary>Arguments for region-based tools (read data / zone / crop / text select / zoom box).</summary>
public sealed record RegionEventArgs(ToolKind Tool, int Page, PdfRect Rect);

/// <summary>The interactive PDF canvas.</summary>
public sealed class PdfCanvas : FrameworkElement
{
    // ------------------------------------------------------------ public state
    private PdfDocument? _doc, _docB;
    private int _page;
    private double _zoom = 1;
    private int _rotation;
    private Vector _offset;
    private ToolKind _tool = ToolKind.Select;
    private IList<Markup>? _markups;
    private Markup? _selected;

    public PdfDocument? Document { get => _doc; set { _doc = value; _page = 0; ResetImages(); InvalidateVisual(); } }
    public PdfDocument? DocumentB { get => _docB; set { _docB = value; ResetImages(); RequestRender(); } }
    public int PageIndex => _page;
    public double Zoom => _zoom;
    public int ViewRotation => _rotation;
    public ToolKind Tool { get => _tool; set { CancelDrawing(); _tool = value; Cursor = CursorFor(value); InvalidateVisual(); } }
    public Markup? SelectedMarkup { get => _selected; set { _selected = value; InvalidateVisual(); } }
    /// <summary>Page index of B shown with page A (compare / overlay).</summary>
    public int PageB { get; set; }

    // style for new markups
    public string CurrentColor { get; set; } = "#E53935";
    public double CurrentLineWidth { get; set; } = 2;
    public double CurrentFontSize { get; set; } = 12;
    public string CurrentTag { get; set; } = "RFI";
    public string CurrentStamp { get; set; } = "APPROVED";
    public bool OrthoSnap { get; set; }
    public bool TemporaryPan { get; set; }
    public Func<int, ScaleSetting>? ScaleProvider { get; set; }
    public Func<int, IReadOnlyList<TextWord>>? ExtraWords { get; set; }
    public Brush CanvasBackground { get; set; } = new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x36));

    // overlays
    private OverlayMode _overlay = OverlayMode.None;
    public OverlayMode Overlay { get => _overlay; set { _overlay = value; _blinkTimer.IsEnabled = value == OverlayMode.Blink; ResetImages(); RequestRender(); } }
    private double _ovOpacity = 0.5, _ovX, _ovY, _ovRot, _ovScale = 1;
    public double OverlayOpacity { get => _ovOpacity; set { _ovOpacity = Math.Clamp(value, 0, 1); InvalidateVisual(); } }
    public double OverlayOffsetX { get => _ovX; set { _ovX = value; InvalidateVisual(); RequestTile(); } }
    public double OverlayOffsetY { get => _ovY; set { _ovY = value; InvalidateVisual(); RequestTile(); } }
    public double OverlayRotation { get => _ovRot; set { _ovRot = value; InvalidateVisual(); RequestTile(); } }
    public double OverlayScale { get => _ovScale; set { _ovScale = Math.Clamp(value, 0.1, 10); InvalidateVisual(); RequestTile(); } }

    public IList<ChangeRegion> Changes { get; set; } = new List<ChangeRegion>();
    public int CurrentChange { get; set; } = -1;
    public IList<SearchHit> SearchHits { get; set; } = new List<SearchHit>();
    public IEnumerable<Zone> Zones { get; set; } = Array.Empty<Zone>();
    public List<PdfRect> TextSelection { get; } = new();
    public bool ShowZones { get; set; } = true;

    // ------------------------------------------------------------ events
    public event Action<Markup>? MarkupCreated;
    public event Action<Markup?>? SelectionChanged;
    public event Action<Markup>? MarkupEdited;          // move/resize done
    public event Action<Markup>? EditTextRequested;     // double click on text markup
    public event Action<Markup>? HyperlinkActivated;
    public event Action<RegionEventArgs>? RegionSelected;
    public event Action<double>? CalibrationLine;       // length in points
    public event Action<PdfPoint?>? PointerMoved;
    public event Action<string>? LiveMeasurement;
    public event Action? ViewChanged;                   // page / zoom / rotation changed
    public event Action? DeleteRequested;

    // ------------------------------------------------------------ images
    private BitmapSource? _baseA, _baseB, _baseATint, _baseBTint;
    private (BitmapSource Bmp, Rect Rect, int Page)? _tileA, _tileB;
    private (BitmapSource Bmp, Rect Rect)? _tileATint, _tileBTint;
    private CancellationTokenSource? _renderCts, _tileCts;
    private readonly DispatcherTimer _tileTimer;
    private readonly DispatcherTimer _blinkTimer;
    private bool _blinkB;
    private bool _loading;
    private static readonly Color TintA = Color.FromRgb(220, 30, 30);
    private static readonly Color TintB = Color.FromRgb(20, 110, 230);

    // ------------------------------------------------------------ interaction state
    private Markup? _drawing;               // markup being drawn
    private bool _waitingSecondClick;
    private Point _dragStartScreen;
    private PdfPoint _dragStartPage;
    private bool _panning;
    private Vector _panStartOffset;
    private bool _moving;
    private int _resizeHandle = -1;
    private List<PdfPoint>? _origPoints;
    private List<PdfRect>? _origRects;
    private PdfRect? _rubber;               // region tools / zoom box
    private PdfPoint? _hover;

    public PdfCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        _tileTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(160), DispatcherPriority.Background, (_, _) => { _tileTimer.Stop(); RenderTiles(); }, Dispatcher) { IsEnabled = false };
        _blinkTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Normal, (_, _) => { _blinkB = !_blinkB; InvalidateVisual(); }, Dispatcher) { IsEnabled = false };
        SizeChanged += (_, _) => { RequestTile(); InvalidateVisual(); };
    }

    // =================================================================== markups source
    public IList<Markup>? Markups
    {
        get => _markups;
        set
        {
            if (_markups is INotifyCollectionChanged old) { old.CollectionChanged -= OnMarkupsChanged; foreach (var m in _markups) m.PropertyChanged -= OnMarkupPropertyChanged; }
            _markups = value;
            if (_markups is INotifyCollectionChanged nw) { nw.CollectionChanged += OnMarkupsChanged; foreach (var m in _markups) m.PropertyChanged += OnMarkupPropertyChanged; }
            InvalidateVisual();
        }
    }

    private void OnMarkupsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (Markup m in e.OldItems) m.PropertyChanged -= OnMarkupPropertyChanged;
        if (e.NewItems != null) foreach (Markup m in e.NewItems) m.PropertyChanged += OnMarkupPropertyChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset && _markups != null)
            foreach (var m in _markups) { m.PropertyChanged -= OnMarkupPropertyChanged; m.PropertyChanged += OnMarkupPropertyChanged; }
        if (_selected != null && _markups != null && !_markups.Contains(_selected)) { _selected = null; SelectionChanged?.Invoke(null); }
        InvalidateVisual();
    }

    private void OnMarkupPropertyChanged(object? s, PropertyChangedEventArgs e) => InvalidateVisual();

    // =================================================================== transforms
    private Size PageSize => _doc?.GetPageSize(_page) ?? new Size(612, 792);

    /// <summary>Rotation + translation part (zoom independent) mapping page coords to "rotated page" coords.</summary>
    private Matrix RotationMatrix()
    {
        var s = PageSize;
        var m = Matrix.Identity;
        m.Rotate(_rotation);
        var corners = new[] { new Point(0, 0), new Point(s.Width, 0), new Point(0, s.Height), new Point(s.Width, s.Height) }.Select(m.Transform).ToArray();
        m.Translate(-corners.Min(c => c.X), -corners.Min(c => c.Y));
        return m;
    }

    private Size RotatedSize => _rotation % 180 == 0 ? PageSize : new Size(PageSize.Height, PageSize.Width);

    public Matrix PageToScreen
    {
        get
        {
            var m = RotationMatrix();
            m.Scale(_zoom, _zoom);
            m.Translate(_offset.X, _offset.Y);
            return m;
        }
    }

    public PdfPoint ToPage(Point screen)
    {
        var m = PageToScreen; m.Invert();
        var p = m.Transform(screen);
        return new PdfPoint(p.X, p.Y);
    }

    public Point ToScreen(PdfPoint p) => PageToScreen.Transform(new Point(p.X, p.Y));

    /// <summary>B page -> A page coordinates (align by page + user offset/rotation/scale).</summary>
    private Matrix BToA()
    {
        if (_docB == null) return Matrix.Identity;
        var sa = PageSize; var sb = _docB.GetPageSize(Math.Min(PageB, _docB.PageCount - 1));
        double k = sa.Width / Math.Max(1, sb.Width) * OverlayScale;
        var m = Matrix.Identity;
        m.Scale(k, k);
        m.RotateAt(OverlayRotation, sb.Width * k / 2, sb.Height * k / 2);
        m.Translate(OverlayOffsetX, OverlayOffsetY);
        return m;
    }

    // =================================================================== navigation API
    public void GoToPage(int index, bool keepView = false)
    {
        if (_doc == null) return;
        index = Math.Clamp(index, 0, _doc.PageCount - 1);
        bool changed = index != _page;
        _page = index;
        if (_docB != null) PageB = Math.Min(index, _docB.PageCount - 1);
        if (changed) { CancelDrawing(); TextSelection.Clear(); ResetImages(); }
        if (!keepView || changed) FitPage();
        RequestRender();
        ViewChanged?.Invoke();
    }

    public void FitPage()
    {
        if (ActualWidth < 10 || ActualHeight < 10) { Dispatcher.BeginInvoke(FitPage, DispatcherPriority.Loaded); return; }
        var rs = RotatedSize;
        _zoom = Math.Min((ActualWidth - 30) / rs.Width, (ActualHeight - 30) / rs.Height);
        _zoom = Math.Clamp(_zoom, 0.01, 64);
        _offset = new Vector((ActualWidth - rs.Width * _zoom) / 2, (ActualHeight - rs.Height * _zoom) / 2);
        AfterViewChange();
    }

    public void FitWidth()
    {
        var rs = RotatedSize;
        _zoom = Math.Clamp((ActualWidth - 30) / rs.Width, 0.01, 64);
        _offset = new Vector((ActualWidth - rs.Width * _zoom) / 2, 15);
        AfterViewChange();
    }

    /// <summary>100% = 96 DPI screen size (1pt = 1.333 px).</summary>
    public void ActualSize() => SetZoom(96.0 / 72.0, new Point(ActualWidth / 2, ActualHeight / 2));

    public void ZoomBy(double factor, Point? at = null) => SetZoom(_zoom * factor, at ?? new Point(ActualWidth / 2, ActualHeight / 2));

    public void SetZoom(double z, Point at)
    {
        z = Math.Clamp(z, 0.01, 64);
        double f = z / _zoom;
        _offset = new Vector(at.X - (at.X - _offset.X) * f, at.Y - (at.Y - _offset.Y) * f);
        _zoom = z;
        AfterViewChange();
    }

    /// <summary>Zooms so that the page rectangle fills ~80% of the view.</summary>
    public void ZoomToRect(PdfRect r, double fill = 0.8)
    {
        var rm = RotationMatrix();
        var c1 = rm.Transform(new Point(r.X, r.Y)); var c2 = rm.Transform(new Point(r.Right, r.Bottom));
        double w = Math.Max(Math.Abs(c2.X - c1.X), 5), h = Math.Max(Math.Abs(c2.Y - c1.Y), 5);
        _zoom = Math.Clamp(Math.Min(ActualWidth * fill / w, ActualHeight * fill / h), 0.01, 32);
        var center = rm.Transform(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
        _offset = new Vector(ActualWidth / 2 - center.X * _zoom, ActualHeight / 2 - center.Y * _zoom);
        AfterViewChange();
    }

    public void Rotate(int delta)
    {
        _rotation = ((_rotation + delta) % 360 + 360) % 360;
        FitPage();
    }

    private void AfterViewChange()
    {
        InvalidateVisual();
        RequestTile();
        ViewChanged?.Invoke();
    }

    // =================================================================== rendering pipeline
    private void ResetImages()
    {
        _renderCts?.Cancel(); _tileCts?.Cancel();
        _baseA = _baseB = _baseATint = _baseBTint = null;
        _tileA = _tileB = null; _tileATint = _tileBTint = null;
    }

    /// <summary>Drops all cached bitmaps of the current page and renders again (e.g. annotation toggle).</summary>
    public void Rerender()
    {
        ResetImages();
        RequestRender();
        RequestTile();
    }

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>Renders low-res base bitmaps of the current page (async).</summary>
    public async void RequestRender()
    {
        if (_doc == null) { InvalidateVisual(); return; }
        _renderCts?.Cancel();
        var cts = _renderCts = new CancellationTokenSource();
        int page = _page;
        var docA = _doc; var docB = _docB; int pageB = PageB;
        bool needB = docB != null && _overlay != OverlayMode.None && _overlay != OverlayMode.AOnly;
        _loading = true; InvalidateVisual();
        try
        {
            var size = docA.GetPageSize(page);
            double baseScale = 1800 / Math.Max(size.Width, size.Height);
            var a = await docA.RenderAsync(page, baseScale, null, cts.Token);
            if (cts.IsCancellationRequested || page != _page) return;
            _baseA = a;
            _baseATint = _overlay == OverlayMode.Colored ? await Task.Run(() => OverlayService.Tint(a, TintA), cts.Token) : null;
            if (needB && docB != null)
            {
                var sb = docB.GetPageSize(pageB);
                double sc = 1800 / Math.Max(sb.Width, sb.Height);
                var b = await docB.RenderAsync(pageB, sc, null, cts.Token);
                if (cts.IsCancellationRequested) return;
                _baseB = b;
                _baseBTint = _overlay == OverlayMode.Colored ? await Task.Run(() => OverlayService.Tint(b, TintB), cts.Token) : null;
            }
            _loading = false;
            InvalidateVisual();
            RequestTile();
            // Prefetch next page base in the background (page cache).
            if (page + 1 < docA.PageCount) _ = docA.RenderAsync(page + 1, 1800 / Math.Max(docA.GetPageSize(page + 1).Width, docA.GetPageSize(page + 1).Height), null, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _loading = false;
            Logger.Error("Render failed", ex);
            InvalidateVisual();
        }
    }

    private void RequestTile() { _tileTimer.Stop(); _tileTimer.Start(); }

    /// <summary>Renders a sharp bitmap of the visible area at the current zoom.</summary>
    private async void RenderTiles()
    {
        if (_doc == null || ActualWidth < 2) return;
        _tileCts?.Cancel();
        var cts = _tileCts = new CancellationTokenSource();
        double px = _zoom * Dpi;
        var size = PageSize;
        double baseScale = 1800 / Math.Max(size.Width, size.Height);
        if (px <= baseScale * 1.05) { _tileA = null; _tileB = null; _tileATint = _tileBTint = null; InvalidateVisual(); return; }
        var vis = VisiblePageRect();
        if (vis.IsEmpty) return;
        int page = _page; var docA = _doc; var docB = _docB; int pageB = PageB;
        bool needB = docB != null && _overlay != OverlayMode.None && _overlay != OverlayMode.AOnly;
        try
        {
            var bmp = await docA.RenderAsync(page, px, vis, cts.Token);
            if (cts.IsCancellationRequested || page != _page) return;
            _tileA = (bmp, vis, page);
            _tileATint = _overlay == OverlayMode.Colored ? (await Task.Run(() => OverlayService.Tint(bmp, TintA), cts.Token), vis) : null;
            if (needB && docB != null)
            {
                var inv = BToA(); inv.Invert();
                var corners = new[] { vis.TopLeft, vis.TopRight, vis.BottomLeft, vis.BottomRight }.Select(inv.Transform).ToArray();
                var rb = new Rect(new Point(corners.Min(c => c.X), corners.Min(c => c.Y)), new Point(corners.Max(c => c.X), corners.Max(c => c.Y)));
                var sb = docB.GetPageSize(pageB);
                rb.Intersect(new Rect(0, 0, sb.Width, sb.Height));
                if (!rb.IsEmpty)
                {
                    double k = BToA().M11 == 0 ? 1 : Math.Sqrt(Math.Abs(BToA().Determinant));
                    var b = await docB.RenderAsync(pageB, px * k, rb, cts.Token);
                    if (cts.IsCancellationRequested) return;
                    _tileB = (b, rb, pageB);
                    _tileBTint = _overlay == OverlayMode.Colored ? (await Task.Run(() => OverlayService.Tint(b, TintB), cts.Token), rb) : null;
                }
            }
            InvalidateVisual();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Warn("Tile render: " + ex.Message); }
    }

    /// <summary>Visible part of the page in page coordinates.</summary>
    public Rect VisiblePageRect()
    {
        var m = PageToScreen; m.Invert();
        var pts = new[] { new Point(0, 0), new Point(ActualWidth, 0), new Point(0, ActualHeight), new Point(ActualWidth, ActualHeight) }.Select(m.Transform).ToArray();
        var r = new Rect(new Point(pts.Min(p => p.X), pts.Min(p => p.Y)), new Point(pts.Max(p => p.X), pts.Max(p => p.Y)));
        r.Intersect(new Rect(new Point(0, 0), PageSize));
        return r;
    }

    // =================================================================== OnRender
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(CanvasBackground, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_doc == null) return;
        var size = PageSize;
        dc.PushTransform(new MatrixTransform(PageToScreen));
        var pageRect = new Rect(0, 0, size.Width, size.Height);
        dc.DrawRectangle(MarkupRenderer.Brush("#000000", 0.35), null, new Rect(4 / _zoom, 4 / _zoom, size.Width, size.Height));
        dc.DrawRectangle(Brushes.White, null, pageRect);

        bool showA = _overlay switch { OverlayMode.BOnly => false, OverlayMode.Blink => !_blinkB || _docB == null, _ => true };
        bool showB = _docB != null && _overlay switch { OverlayMode.BOnly => true, OverlayMode.Transparency => true, OverlayMode.Colored => true, OverlayMode.Blink => _blinkB, _ => false };
        bool tinted = _overlay == OverlayMode.Colored && _docB != null;

        if (showA)
        {
            var baseImg = tinted ? _baseATint : _baseA;
            if (baseImg != null) dc.DrawImage(baseImg, pageRect);
            var tile = tinted ? (_tileATint.HasValue ? _tileATint.Value : default((BitmapSource, Rect)?)) : (_tileA.HasValue && _tileA.Value.Page == _page ? (_tileA.Value.Bmp, _tileA.Value.Rect) : null);
            if (tile.HasValue) dc.DrawImage(tile.Value.Item1, tile.Value.Item2);
        }
        if (showB)
        {
            dc.PushClip(new RectangleGeometry(pageRect));
            dc.PushTransform(new MatrixTransform(BToA()));
            if (_overlay == OverlayMode.Transparency) dc.PushOpacity(OverlayOpacity);
            var sb = _docB!.GetPageSize(Math.Min(PageB, _docB.PageCount - 1));
            var baseImg = tinted ? _baseBTint : _baseB;
            if (baseImg != null) dc.DrawImage(baseImg, new Rect(0, 0, sb.Width, sb.Height));
            var tile = tinted ? (_tileBTint.HasValue ? _tileBTint.Value : default((BitmapSource, Rect)?)) : (_tileB.HasValue ? (_tileB.Value.Bmp, _tileB.Value.Rect) : null);
            if (tile.HasValue) dc.DrawImage(tile.Value.Item1, tile.Value.Item2);
            if (_overlay == OverlayMode.Transparency) dc.Pop();
            dc.Pop(); dc.Pop();
        }
        if (_loading && _baseA == null)
        {
            var ft = MarkupRenderer.Text("Rendering…", 14 / _zoom, Brushes.Gray);
            dc.DrawText(ft, new Point(size.Width / 2 - ft.Width / 2, size.Height / 2));
        }

        // zones
        if (ShowZones)
        {
            var zp = MarkupRenderer.MakePen("#1565C0", 1.2 / _zoom, 0.9, DashStyles.Dash);
            foreach (var z in Zones.Where(z => z.AllPages || z.Page == _page))
            {
                var r = new Rect(z.Rect.X, z.Rect.Y, z.Rect.Width, z.Rect.Height);
                dc.DrawRectangle(MarkupRenderer.Brush("#1565C0", 0.07), zp, r);
                dc.DrawText(MarkupRenderer.Text(z.Name, 10 / _zoom, MarkupRenderer.Brush("#1565C0"), true), new Point(r.X, r.Y - 13 / _zoom));
            }
        }
        // search hits
        foreach (var h in SearchHits.Where(h => h.Page == _page))
            dc.DrawRectangle(MarkupRenderer.Brush("#FFEB3B", 0.45), MarkupRenderer.MakePen("#F57F17", 0.8 / _zoom), new Rect(h.Rect.X - 1, h.Rect.Y - 1, h.Rect.Width + 2, h.Rect.Height + 2));
        // text selection
        foreach (var r in TextSelection) dc.DrawRectangle(MarkupRenderer.Brush("#2196F3", 0.3), null, new Rect(r.X, r.Y, r.Width, r.Height));
        // compare change regions
        for (int i = 0; i < Changes.Count; i++)
        {
            var c = Changes[i];
            if (c.Page != _page) continue;
            string col = c.Kind.Contains("dded") ? "#2E7D32" : c.Kind.Contains("emoved") ? "#C62828" : "#EF6C00";
            bool cur = i == CurrentChange;
            var pen = MarkupRenderer.MakePen(col, (cur ? 3 : 1.5) / _zoom, 0.95);
            var rr = c.Rect.Inflate(3 / _zoom);
            dc.DrawGeometry(cur ? MarkupRenderer.Brush(col, 0.12) : null, pen,
                MarkupRenderer.Poly(Geometry.CloudPoints(Geometry.RectCorners(rr), Math.Clamp(Math.Min(rr.Width, rr.Height) / 5, 2, 20)), true, cur));
        }
        // markups
        if (_markups != null)
            foreach (var m in _markups)
                if (m.Page == _page) MarkupRenderer.Draw(dc, m, _zoom, false);
        if (_drawing != null)
        {
            var preview = _drawing;
            if (_hover != null && NeedsHoverPoint(preview))
            {
                preview = CloneForPreview(_drawing);
                preview.Points.Add(Snap(preview.Points.LastOrDefault(), _hover.Value));
                if (ScaleProvider != null) MeasurementService.Update(preview, ScaleProvider(_page));
            }
            MarkupRenderer.Draw(dc, preview, _zoom, true);
        }
        if (_rubber != null)
        {
            var r = _rubber.Value;
            string col = _tool switch { ToolKind.Crop => "#8E24AA", ToolKind.Zone => "#1565C0", ToolKind.RegionRead => "#00897B", _ => "#1E88E5" };
            dc.DrawRectangle(MarkupRenderer.Brush(col, 0.1), MarkupRenderer.MakePen(col, 1.2 / _zoom, 1, DashStyles.Dash), new Rect(r.X, r.Y, r.Width, r.Height));
        }
        // selection
        if (_selected != null && _selected.Page == _page)
        {
            var b = _selected.Bounds.Inflate(4 / _zoom);
            dc.DrawRectangle(null, MarkupRenderer.MakePen("#2196F3", 1 / _zoom, 1, DashStyles.Dash), new Rect(b.X, b.Y, b.Width, b.Height));
            double hs = 4.5 / _zoom;
            foreach (var h in Handles(_selected))
                dc.DrawRectangle(Brushes.White, MarkupRenderer.MakePen("#2196F3", 1.2 / _zoom), new Rect(h.X - hs, h.Y - hs, hs * 2, hs * 2));
        }
        dc.Pop();
    }

    // =================================================================== tools helpers
    private static Cursor CursorFor(ToolKind t) => t switch
    {
        ToolKind.Select => Cursors.Arrow,
        ToolKind.Pan => Cursors.Hand,
        ToolKind.TextSelect => Cursors.IBeam,
        ToolKind.Typewriter => Cursors.IBeam,
        _ => Cursors.Cross
    };

    private static MarkupType? TypeFor(ToolKind t) => t switch
    {
        ToolKind.TextBox => MarkupType.TextBox, ToolKind.Callout => MarkupType.Callout, ToolKind.Typewriter => MarkupType.Typewriter,
        ToolKind.Label => MarkupType.Label, ToolKind.Line => MarkupType.Line, ToolKind.Arrow => MarkupType.Arrow,
        ToolKind.Polyline => MarkupType.Polyline, ToolKind.Rectangle => MarkupType.Rectangle, ToolKind.Ellipse => MarkupType.Ellipse,
        ToolKind.Cloud => MarkupType.Cloud, ToolKind.Polygon => MarkupType.Polygon, ToolKind.Pen => MarkupType.Pen,
        ToolKind.Highlighter => MarkupType.Highlighter, ToolKind.Highlight => MarkupType.Highlight, ToolKind.Tag => MarkupType.Tag,
        ToolKind.Stamp => MarkupType.Stamp, ToolKind.IssuePin => MarkupType.IssuePin, ToolKind.Hyperlink => MarkupType.Hyperlink,
        ToolKind.Length => MarkupType.Length, ToolKind.PolyLength => MarkupType.PolyLength, ToolKind.Area => MarkupType.Area,
        ToolKind.Perimeter => MarkupType.Perimeter, ToolKind.Count => MarkupType.Count, ToolKind.Angle => MarkupType.Angle,
        ToolKind.Calibrate => MarkupType.Length,
        _ => null
    };

    private static bool IsRegionTool(ToolKind t) => t is ToolKind.RegionRead or ToolKind.Zone or ToolKind.Crop or ToolKind.TextSelect or ToolKind.ZoomBox;
    private static bool IsTwoPointTool(ToolKind t) => t is ToolKind.Line or ToolKind.Arrow or ToolKind.Length or ToolKind.Calibrate;
    private static bool IsPolyTool(ToolKind t) => t is ToolKind.Polyline or ToolKind.Polygon or ToolKind.PolyLength or ToolKind.Area or ToolKind.Perimeter;
    private static bool IsBoxTool(ToolKind t) => t is ToolKind.TextBox or ToolKind.Label or ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Cloud
        or ToolKind.Highlight or ToolKind.Tag or ToolKind.Stamp or ToolKind.Hyperlink;
    private bool NeedsHoverPoint(Markup m) => m.Type is not (MarkupType.Pen or MarkupType.Highlighter or MarkupType.Count) && !IsBoxTool(_tool)
        && (_waitingSecondClick || IsPolyTool(_tool) || _tool is ToolKind.Angle or ToolKind.Callout);

    private static Markup CloneForPreview(Markup src) => new()
    {
        Type = src.Type, Page = src.Page, Points = new List<PdfPoint>(src.Points), Color = src.Color, LineWidth = src.LineWidth,
        FontSize = src.FontSize, Opacity = src.Opacity, Subject = src.Subject, Text = src.Text, FillColor = src.FillColor
    };

    private Markup NewMarkup(MarkupType type, PdfPoint start) => new()
    {
        Type = type, Page = _page, Color = type switch
        {
            MarkupType.Highlight or MarkupType.Highlighter => CurrentColor == "#E53935" ? "#FFEB3B" : CurrentColor,
            MarkupType.Hyperlink => "#1E88E5",
            _ => CurrentColor
        },
        LineWidth = CurrentLineWidth, FontSize = CurrentFontSize,
        Subject = type switch
        {
            MarkupType.Tag => CurrentTag, MarkupType.Stamp => CurrentStamp, MarkupType.Hyperlink => "Hyperlink",
            _ => type.ToString()
        },
        Points = new List<PdfPoint> { start }
    };

    /// <summary>Shift / ortho: constrain to 0/45/90°.</summary>
    private PdfPoint Snap(PdfPoint from, PdfPoint to)
    {
        if (!(OrthoSnap || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) || _drawing == null || _drawing.Points.Count == 0) return to;
        double dx = to.X - from.X, dy = to.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
        double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return new PdfPoint(from.X + len * Math.Cos(ang), from.Y + len * Math.Sin(ang));
    }

    public void CancelDrawing()
    {
        _drawing = null; _rubber = null; _waitingSecondClick = false;
        InvalidateVisual();
    }

    /// <summary>Finishes the current poly / count markup.</summary>
    public void FinishDrawing()
    {
        if (_drawing == null) return;
        var m = _drawing;
        _drawing = null; _waitingSecondClick = false;
        int min = m.Type switch { MarkupType.Polygon or MarkupType.Area or MarkupType.Perimeter => 3, MarkupType.Count => 1, MarkupType.Angle => 3, _ => 2 };
        // remove duplicated last point produced by double-click
        if (m.Points.Count >= 2 && Geometry.Dist(m.Points[^1], m.Points[^2]) < 0.5) m.Points.RemoveAt(m.Points.Count - 1);
        if (m.Points.Count >= min) Emit(m);
        InvalidateVisual();
    }

    private void Emit(Markup m)
    {
        if (_tool == ToolKind.Calibrate)
        {
            if (m.Points.Count >= 2) CalibrationLine?.Invoke(Geometry.Dist(m.Points[0], m.Points[1]));
            return;
        }
        if (ScaleProvider != null) MeasurementService.Update(m, ScaleProvider(m.Page));
        MarkupCreated?.Invoke(m);
    }

    // =================================================================== hit testing
    public Markup? HitTest(PdfPoint p)
    {
        if (_markups == null) return null;
        double tol = 6 / _zoom;
        for (int i = _markups.Count - 1; i >= 0; i--)
        {
            var m = _markups[i];
            if (m.Page != _page) continue;
            if (Hit(m, p, tol)) return m;
        }
        return null;
    }

    private static bool Hit(Markup m, PdfPoint p, double tol)
    {
        var pts = m.Points;
        var b = m.Bounds;
        if (!b.Inflate(tol + 12).Contains(p.X, p.Y)) return false;
        switch (m.Type)
        {
            case MarkupType.Rectangle or MarkupType.Ellipse or MarkupType.Cloud when m.FillColor == null:
                return b.Inflate(tol).Contains(p.X, p.Y) && !b.Inflate(-tol - 4).Contains(p.X, p.Y);
            case MarkupType.Highlight:
                return m.Rects != null && m.Rects.Count > 0 ? m.Rects.Any(r => r.Inflate(tol).Contains(p.X, p.Y)) : b.Contains(p.X, p.Y);
            case MarkupType.Polygon or MarkupType.Area or MarkupType.Perimeter
                or MarkupType.Line or MarkupType.Arrow or MarkupType.Polyline or MarkupType.Pen or MarkupType.Highlighter
                or MarkupType.Length or MarkupType.PolyLength or MarkupType.Angle:
                bool closed = m.Type is MarkupType.Perimeter or MarkupType.Polygon or MarkupType.Area;
                if (closed && pts.Count > 2 && Geometry.PointInPolygon(p, pts)) return true;
                double t = m.Type == MarkupType.Highlighter ? Math.Max(tol, m.LineWidth) : tol + m.LineWidth / 2;
                for (int i = 1; i < pts.Count; i++) if (Geometry.DistToSegment(p, pts[i - 1], pts[i]) <= t) return true;
                if (closed && pts.Count > 2 && Geometry.DistToSegment(p, pts[^1], pts[0]) <= t) return true;
                return false;
            case MarkupType.Count:
                return pts.Any(q => Geometry.Dist(q, p) <= tol + 6);
            case MarkupType.IssuePin:
                return pts.Count > 0 && Geometry.Dist(pts[0], p) <= 12;
            case MarkupType.Tag:
                return b.Inflate(tol).Contains(p.X, p.Y) || new PdfRect(b.X, b.Y - m.FontSize - 10, 200, m.FontSize + 10).Contains(p.X, p.Y);
            default:
                return b.Inflate(tol).Contains(p.X, p.Y);
        }
    }

    /// <summary>Resize handles (page coords).</summary>
    private static List<PdfPoint> Handles(Markup m)
    {
        if (m.Type is MarkupType.Pen or MarkupType.Highlighter || (m.Type == MarkupType.Highlight && m.Rects is { Count: > 0 })) return new();
        if (Geometry.IsBoxType(m.Type) && m.Points.Count >= 2) return Geometry.RectCorners(PdfRect.FromPoints(m.Points[0], m.Points[1]));
        return m.Points.Count <= 300 ? new List<PdfPoint>(m.Points) : new();
    }

    // =================================================================== mouse
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_doc == null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { _offset.X += e.Delta * 0.5; AfterViewChange(); }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { _offset.Y += e.Delta * 0.5; AfterViewChange(); }
        else SetZoom(_zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (_doc == null) return;
        var sp = e.GetPosition(this);
        var pp = ToPage(sp);

        // Pan: middle button, Pan tool, or Space held
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && (_tool == ToolKind.Pan || TemporaryPan)))
        {
            _panning = true; _dragStartScreen = sp; _panStartOffset = _offset;
            Cursor = Cursors.SizeAll; CaptureMouse(); e.Handled = true; return;
        }
        if (e.ChangedButton == MouseButton.Right)
        {
            if (_drawing != null) { FinishDrawing(); e.Handled = true; }
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        if (_tool == ToolKind.Select) { SelectDown(pp, sp, e.ClickCount); return; }

        if (IsRegionTool(_tool))
        {
            _dragStartPage = pp; _rubber = new PdfRect(pp.X, pp.Y, 0, 0); CaptureMouse(); return;
        }

        var type = TypeFor(_tool);
        if (type == null) return;

        if (_tool == ToolKind.Count)
        {
            if (_drawing == null) _drawing = NewMarkup(MarkupType.Count, pp);
            else _drawing.Points.Add(pp);
            if (e.ClickCount == 2) FinishDrawing();
            if (_drawing != null && ScaleProvider != null) { MeasurementService.Update(_drawing, ScaleProvider(_page)); LiveMeasurement?.Invoke(_drawing.ValueText); }
            InvalidateVisual(); return;
        }
        if (_tool is ToolKind.Typewriter or ToolKind.IssuePin)
        {
            var m = NewMarkup(type.Value, pp);
            if (_tool == ToolKind.IssuePin) m.Color = "#D32F2F";
            Emit(m); return;
        }
        if (IsPolyTool(_tool) || _tool == ToolKind.Angle)
        {
            if (_drawing == null) _drawing = NewMarkup(type.Value, pp);
            else _drawing.Points.Add(Snap(_drawing.Points[^1], pp));
            if (e.ClickCount == 2 || (_tool == ToolKind.Angle && _drawing.Points.Count >= 3)) FinishDrawing();
            InvalidateVisual(); return;
        }
        if (_tool == ToolKind.Callout)
        {
            if (_drawing == null) { _drawing = NewMarkup(MarkupType.Callout, pp); }
            else
            {
                var m = _drawing; _drawing = null;
                m.Points.Add(pp);
                m.Points.Add(new PdfPoint(pp.X + 160, pp.Y + 44));
                Emit(m);
            }
            InvalidateVisual(); return;
        }
        if (IsTwoPointTool(_tool))
        {
            if (_waitingSecondClick && _drawing != null)
            {
                _drawing.Points.Add(Snap(_drawing.Points[0], pp));
                var m = _drawing; _drawing = null; _waitingSecondClick = false;
                Emit(m); InvalidateVisual(); return;
            }
            _drawing = NewMarkup(type.Value, pp); _dragStartScreen = sp; CaptureMouse(); return;
        }
        if (_tool is ToolKind.Pen or ToolKind.Highlighter)
        {
            _drawing = NewMarkup(type.Value, pp); CaptureMouse(); return;
        }
        if (IsBoxTool(_tool))
        {
            _drawing = NewMarkup(type.Value, pp); _drawing.Points.Add(pp); _dragStartScreen = sp; CaptureMouse(); return;
        }
    }

    private void SelectDown(PdfPoint pp, Point sp, int clicks)
    {
        // handles of the selected markup first
        if (_selected != null && _selected.Page == _page)
        {
            var hs = Handles(_selected);
            for (int i = 0; i < hs.Count; i++)
                if (Geometry.Dist(hs[i], pp) <= 7 / _zoom)
                {
                    _resizeHandle = i; _origPoints = new(_selected.Points); _dragStartPage = pp; CaptureMouse(); return;
                }
        }
        var hit = HitTest(pp);
        if (hit != _selected) { _selected = hit; SelectionChanged?.Invoke(hit); }
        if (hit != null)
        {
            if (clicks == 2)
            {
                if (hit.Type == MarkupType.Hyperlink || !string.IsNullOrEmpty(hit.LinkTarget)) HyperlinkActivated?.Invoke(hit);
                else if (hit.Type is MarkupType.TextBox or MarkupType.Callout or MarkupType.Typewriter or MarkupType.Label or MarkupType.Tag) EditTextRequested?.Invoke(hit);
                return;
            }
            _moving = true; _dragStartPage = pp; _origPoints = new(hit.Points); _origRects = hit.Rects == null ? null : new(hit.Rects);
            CaptureMouse();
        }
        else
        {
            // empty space: drag = pan (convenient in select mode)
            _panning = true; _dragStartScreen = sp; _panStartOffset = _offset; CaptureMouse();
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_doc == null) return;
        var sp = e.GetPosition(this);
        var pp = ToPage(sp);
        var size = PageSize;
        PointerMoved?.Invoke(pp.X >= 0 && pp.Y >= 0 && pp.X <= size.Width && pp.Y <= size.Height ? pp : null);

        if (_panning)
        {
            _offset = _panStartOffset + (sp - _dragStartScreen);
            InvalidateVisual(); RequestTile(); return;
        }
        if (_moving && _selected != null && _origPoints != null)
        {
            double dx = pp.X - _dragStartPage.X, dy = pp.Y - _dragStartPage.Y;
            _selected.Points = _origPoints.Select(q => new PdfPoint(q.X + dx, q.Y + dy)).ToList();
            if (_origRects != null) _selected.Rects = _origRects.Select(r => new PdfRect(r.X + dx, r.Y + dy, r.Width, r.Height)).ToList();
            InvalidateVisual(); return;
        }
        if (_resizeHandle >= 0 && _selected != null && _origPoints != null)
        {
            var pts = new List<PdfPoint>(_origPoints);
            if (Geometry.IsBoxType(_selected.Type) && pts.Count >= 2)
            {
                var r = PdfRect.FromPoints(pts[0], pts[1]);
                double x0 = r.X, y0 = r.Y, x1 = r.Right, y1 = r.Bottom;
                switch (_resizeHandle) { case 0: x0 = pp.X; y0 = pp.Y; break; case 1: x1 = pp.X; y0 = pp.Y; break; case 2: x1 = pp.X; y1 = pp.Y; break; case 3: x0 = pp.X; y1 = pp.Y; break; }
                pts[0] = new PdfPoint(x0, y0); pts[1] = new PdfPoint(x1, y1);
            }
            else if (_resizeHandle < pts.Count) pts[_resizeHandle] = pp;
            _selected.Points = pts;
            InvalidateVisual(); return;
        }
        if (_rubber != null && e.LeftButton == MouseButtonState.Pressed)
        {
            _rubber = PdfRect.FromPoints(_dragStartPage, pp); InvalidateVisual(); return;
        }
        if (_drawing != null)
        {
            if (_tool is ToolKind.Pen or ToolKind.Highlighter && e.LeftButton == MouseButtonState.Pressed)
            {
                if (Geometry.Dist(_drawing.Points[^1], pp) > 1.5 / _zoom) _drawing.Points.Add(pp);
            }
            else if (IsBoxTool(_tool) && e.LeftButton == MouseButtonState.Pressed && _drawing.Points.Count >= 2)
            {
                _drawing.Points[1] = pp;
            }
            else if (IsTwoPointTool(_tool) && e.LeftButton == MouseButtonState.Pressed && !_waitingSecondClick)
            {
                _hover = pp; _waitingSecondClick = true; // drag mode shows preview too
            }
            _hover = pp;
            if (NeedsHoverPoint(_drawing) && ScaleProvider != null && _drawing.IsMeasurement)
            {
                var prev = CloneForPreview(_drawing);
                prev.Points.Add(Snap(prev.Points[^1], pp));
                MeasurementService.Update(prev, ScaleProvider(_page));
                LiveMeasurement?.Invoke(prev.ValueText);
            }
            InvalidateVisual();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_doc == null) return;
        var sp = e.GetPosition(this);
        var pp = ToPage(sp);
        if (_panning) { _panning = false; ReleaseMouseCapture(); Cursor = CursorFor(_tool); RequestTile(); ViewChanged?.Invoke(); return; }
        ReleaseMouseCapture();
        if (_moving || _resizeHandle >= 0)
        {
            bool changed = _selected != null && _origPoints != null && !_selected.Points.SequenceEqual(_origPoints);
            _moving = false; _resizeHandle = -1; _origPoints = null; _origRects = null;
            if (changed && _selected != null) MarkupEdited?.Invoke(_selected);
            return;
        }
        if (_rubber != null)
        {
            var r = _rubber.Value; _rubber = null;
            InvalidateVisual();
            if (r.Width * _zoom < 4 || r.Height * _zoom < 4) return;
            if (_tool == ToolKind.ZoomBox) { ZoomToRect(r, 0.95); return; }
            if (_tool == ToolKind.TextSelect)
            {
                TextSelection.Clear();
                TextSelection.AddRange(WordsIn(r).Select(w => w.Rect));
            }
            RegionSelected?.Invoke(new RegionEventArgs(_tool, _page, r));
            return;
        }
        if (_drawing == null || e.ChangedButton != MouseButton.Left) return;

        if (_tool is ToolKind.Pen or ToolKind.Highlighter)
        {
            var m = _drawing; _drawing = null;
            if (m.Points.Count >= 2) Emit(m);
            InvalidateVisual(); return;
        }
        if (IsTwoPointTool(_tool) && _drawing.Points.Count == 1)
        {
            if ((sp - _dragStartScreen).Length > 6)
            {
                _drawing.Points.Add(Snap(_drawing.Points[0], pp));
                var m = _drawing; _drawing = null; _waitingSecondClick = false;
                Emit(m);
            }
            else _waitingSecondClick = true; // click-click mode
            InvalidateVisual(); return;
        }
        if (IsBoxTool(_tool) && _drawing.Points.Count >= 2)
        {
            var m = _drawing; _drawing = null;
            var r = PdfRect.FromPoints(m.Points[0], m.Points[1]);
            if (r.Width * _zoom < 5 || r.Height * _zoom < 5)
            {
                // single click: default size
                var (w, h) = m.Type switch
                {
                    MarkupType.Stamp => (220.0, 60.0), MarkupType.TextBox => (180.0, 50.0), MarkupType.Label => (90.0, 22.0),
                    MarkupType.Tag => (120.0, 70.0), _ => (0.0, 0.0)
                };
                if (w == 0) { InvalidateVisual(); return; }
                w /= Math.Max(0.25, Math.Min(_zoom, 2)); h /= Math.Max(0.25, Math.Min(_zoom, 2));
                m.Points = new List<PdfPoint> { m.Points[0], new(m.Points[0].X + w, m.Points[0].Y + h) };
            }
            else m.Points = new List<PdfPoint> { new(r.X, r.Y), new(r.Right, r.Bottom) };
            if (m.Type == MarkupType.Highlight)
            {
                var lines = SnapHighlight(r);
                if (lines.Count > 0) m.Rects = lines;
            }
            Emit(m);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); PointerMoved?.Invoke(null); }

    // =================================================================== keyboard
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                if (_drawing != null && _drawing.Type == MarkupType.Count) FinishDrawing(); else CancelDrawing();
                TextSelection.Clear(); e.Handled = true; break;
            case Key.Enter: FinishDrawing(); e.Handled = true; break;
            case Key.Back when _drawing != null && _drawing.Points.Count > 1:
                _drawing.Points.RemoveAt(_drawing.Points.Count - 1); InvalidateVisual(); e.Handled = true; break;
            case Key.Delete when _selected != null: DeleteRequested?.Invoke(); e.Handled = true; break;
            case Key.PageDown: GoToPage(_page + 1); e.Handled = true; break;
            case Key.PageUp: GoToPage(_page - 1); e.Handled = true; break;
        }
    }

    // =================================================================== text helpers
    public List<TextWord> WordsIn(PdfRect r)
    {
        if (_doc == null) return new();
        var words = _doc.GetWords(_page).AsEnumerable();
        if (ExtraWords != null) words = words.Concat(ExtraWords(_page));
        return words.Where(w => w.Rect.IntersectsWith(r)).ToList();
    }

    /// <summary>Highlight snapped to text lines (Bluebeam-like): one rect per text line.</summary>
    private List<PdfRect> SnapHighlight(PdfRect r)
    {
        var words = WordsIn(r);
        var lines = new List<PdfRect>();
        foreach (var w in words.OrderBy(w => w.Rect.Y))
        {
            int idx = lines.FindIndex(l => Math.Abs((l.Y + l.Height / 2) - (w.Rect.Y + w.Rect.Height / 2)) < Math.Max(2, w.Rect.Height * 0.5));
            if (idx >= 0) lines[idx] = lines[idx].Union(w.Rect); else lines.Add(w.Rect);
        }
        return lines.Select(l => l.Inflate(1)).ToList();
    }
}
