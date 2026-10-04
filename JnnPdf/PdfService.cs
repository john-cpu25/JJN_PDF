// ============================================================================
//  PdfService.cs  â€“  PDF ENGINE (Google PDFium via PDFiumCore)
//  * Open (password / corrupt / missing handling)
//  * Render page or tile (async, cancellable, cached)
//  * Text layer extraction (words with display-space boxes), search
//  * Metadata, bookmarks, page sizes
//  PDFium is NOT thread-safe -> every native call goes through one global lock.
// ============================================================================
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFiumCore;

namespace JnnPdf;

public enum PdfErrorKind { NotFound, Password, Corrupt, Security, Unknown }

/// <summary>Exception thrown when a PDF can't be opened.</summary>
public sealed class PdfOpenException : Exception
{
    public PdfErrorKind Kind { get; }
    public PdfOpenException(PdfErrorKind kind, string message) : base(message) => Kind = kind;
}

/// <summary>Affine transform between PDF user space and page display space (points).</summary>
public readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    // x' = A*x + C*y + E ; y' = B*x + D*y + F
    public PdfPoint Apply(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);
    public Affine Invert()
    {
        double det = A * D - B * C;
        if (Math.Abs(det) < 1e-12) return this;
        double ia = D / det, ib = -B / det, ic = -C / det, id = A / det;
        return new Affine(ia, ib, ic, id, -(ia * E + ic * F), -(ib * E + id * F));
    }
}

/// <summary>One opened PDF document. Thread-safe wrapper around PDFium.</summary>
public sealed class PdfDocument : IDisposable
{
    /// <summary>Global PDFium lock (library is single threaded).</summary>
    public static readonly object Gate = new();
    private static bool _libraryInitialised;

    private const int FPDF_ANNOT = 0x01;
    private const int FPDF_PRINTING = 0x800;

    private FpdfDocumentT? _doc;
    private readonly string? _tempCopy;
    private readonly PdfPageInfo[] _pages;
    private readonly Affine?[] _toDisplay;
    private readonly Dictionary<int, FpdfPageT> _openPages = new();
    private readonly LinkedList<int> _pageLru = new();
    private readonly Dictionary<int, IReadOnlyList<TextWord>> _textCache = new();
    private readonly LruCache<string, BitmapSource> _bitmapCache = new(48);

    public string FilePath { get; }
    public int PageCount => _pages.Length;
    public PdfDocumentInfo Info { get; }
    /// <summary>Render PDF annotations (comments made in other tools) on the page.</summary>
    public bool RenderAnnotations { get; set; } = true;

    private PdfDocument(string path, FpdfDocumentT doc, string? tempCopy)
    {
        FilePath = path;
        _doc = doc;
        _tempCopy = tempCopy;
        int n = fpdfview.FPDF_GetPageCount(doc);
        _pages = new PdfPageInfo[n];
        _toDisplay = new Affine?[n];
        for (int i = 0; i < n; i++)
        {
            var size = new FS_SIZEF_();
            fpdfview.FPDF_GetPageSizeByIndexF(doc, i, size);
            _pages[i] = new PdfPageInfo(i, size.Width, size.Height, 0);
        }
        var meta = new Dictionary<string, string>();
        foreach (var tag in new[] { "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate" })
        {
            string v = ReadMeta(doc, tag);
            if (!string.IsNullOrWhiteSpace(v)) meta[tag] = v;
        }
        Info = new PdfDocumentInfo
        {
            Path = path, PageCount = n, Metadata = meta,
            FileSize = File.Exists(path) ? new FileInfo(path).Length : 0
        };
    }

    // ------------------------------------------------------------------ open
    /// <summary>Opens a PDF. Throws <see cref="PdfOpenException"/> with a clear reason.</summary>
    public static PdfDocument Open(string path, string? password = null)
    {
        if (!File.Exists(path)) throw new PdfOpenException(PdfErrorKind.NotFound, $"File not found:\n{path}");
        lock (Gate)
        {
            if (!_libraryInitialised) { fpdfview.FPDF_InitLibrary(); _libraryInitialised = true; }
            string? temp = null;
            var doc = fpdfview.FPDF_LoadDocument(path, password);
            if (doc == null || doc.__Instance == IntPtr.Zero)
            {
                ulong err = fpdfview.FPDF_GetLastError();
                // PDFium may fail on non-ASCII paths: retry from an ASCII temp copy.
                if (err == 2 && path.Any(c => c > 127))
                {
                    temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jnnpdf_" + Guid.NewGuid().ToString("N") + ".pdf");
                    File.Copy(path, temp, true);
                    doc = fpdfview.FPDF_LoadDocument(temp, password);
                    err = doc == null || doc.__Instance == IntPtr.Zero ? fpdfview.FPDF_GetLastError() : 0;
                }
                if (err != 0)
                {
                    if (temp != null) TryDelete(temp);
                    throw err switch
                    {
                        2 => new PdfOpenException(PdfErrorKind.NotFound, "Cannot open the file (it is locked or you have no read permission)."),
                        3 => new PdfOpenException(PdfErrorKind.Corrupt, "The PDF file is corrupt or not a valid PDF."),
                        4 => new PdfOpenException(PdfErrorKind.Password, "The PDF is password protected (or the password is wrong)."),
                        5 => new PdfOpenException(PdfErrorKind.Security, "The PDF uses an unsupported security handler."),
                        _ => new PdfOpenException(PdfErrorKind.Unknown, $"PDFium error #{err} while opening the file.")
                    };
                }
            }
            return new PdfDocument(path, doc!, temp);
        }
    }

    private static string ReadMeta(FpdfDocumentT doc, string tag)
    {
        try
        {
            ulong len = fpdf_doc.FPDF_GetMetaText(doc, tag, IntPtr.Zero, 0);
            if (len <= 2) return "";
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                fpdf_doc.FPDF_GetMetaText(doc, tag, buf, len);
                return Marshal.PtrToStringUni(buf, (int)len / 2 - 1) ?? "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ pages
    public PdfPageInfo GetPageInfo(int index) => _pages[Math.Clamp(index, 0, _pages.Length - 1)];
    public Size GetPageSize(int index) { var p = GetPageInfo(index); return new Size(p.Width, p.Height); }

    /// <summary>Gets (and caches) a parsed page handle. Caller MUST hold <see cref="Gate"/>.</summary>
    private FpdfPageT GetPageHandle(int index)
    {
        if (_doc == null) throw new ObjectDisposedException(nameof(PdfDocument));
        if (index < 0 || index >= _pages.Length) throw new ArgumentOutOfRangeException(nameof(index), "Invalid page.");
        if (_openPages.TryGetValue(index, out var p))
        {
            _pageLru.Remove(index); _pageLru.AddFirst(index);
            return p;
        }
        p = fpdfview.FPDF_LoadPage(_doc, index) ?? throw new InvalidOperationException($"Cannot read page {index + 1}.");
        if (p.__Instance == IntPtr.Zero) throw new InvalidOperationException($"Cannot read page {index + 1}.");
        _openPages[index] = p;
        _pageLru.AddFirst(index);
        while (_pageLru.Count > 4)
        {
            int old = _pageLru.Last!.Value;
            _pageLru.RemoveLast();
            if (_openPages.Remove(old, out var op)) fpdfview.FPDF_ClosePage(op);
        }
        if (_toDisplay[index] == null) _toDisplay[index] = ComputeAffine(p, _pages[index]);
        return p;
    }

    /// <summary>Derives PDF-user-space -> display-space affine using FPDF_PageToDevice on 3 points.</summary>
    private static Affine ComputeAffine(FpdfPageT page, PdfPageInfo info)
    {
        const int k = 64; // device oversampling for precision
        int sx = (int)Math.Round(info.Width * k), sy = (int)Math.Round(info.Height * k);
        PdfPoint Map(double x, double y)
        {
            int dx = 0, dy = 0;
            fpdfview.FPDF_PageToDevice(page, 0, 0, sx, sy, 0, x, y, ref dx, ref dy);
            return new PdfPoint(dx / (double)k, dy / (double)k);
        }
        var p0 = Map(0, 0); var p1 = Map(1000, 0); var p2 = Map(0, 1000);
        return new Affine((p1.X - p0.X) / 1000, (p1.Y - p0.Y) / 1000, (p2.X - p0.X) / 1000, (p2.Y - p0.Y) / 1000, p0.X, p0.Y);
    }

    /// <summary>PDF user space -> display transform for a page.</summary>
    public Affine GetUserToDisplay(int page)
    {
        lock (Gate) { GetPageHandle(page); return _toDisplay[page]!.Value; }
    }

    // ------------------------------------------------------------------ render
    /// <summary>Renders a page region. <paramref name="clip"/> in display points, scale = pixels per point.</summary>
    public BitmapSource Render(int pageIndex, double scale, Rect? clip = null)
    {
        var size = GetPageSize(pageIndex);
        Rect c = clip ?? new Rect(0, 0, size.Width, size.Height);
        c.Intersect(new Rect(0, 0, size.Width, size.Height));
        if (c.IsEmpty || c.Width < 0.5 || c.Height < 0.5) c = new Rect(0, 0, Math.Min(10, size.Width), Math.Min(10, size.Height));
        string key = $"{pageIndex}|{scale:0.####}|{c.X:0.#}|{c.Y:0.#}|{c.Width:0.#}|{c.Height:0.#}|{RenderAnnotations}";
        if (_bitmapCache.TryGet(key, out var cached)) return cached;

        int w = Math.Max(1, (int)Math.Ceiling(c.Width * scale));
        int h = Math.Max(1, (int)Math.Ceiling(c.Height * scale));
        // Hard cap: ~64 MP to avoid out-of-memory on huge sheets.
        double cap = Math.Sqrt(64_000_000.0 / ((double)w * h));
        if (cap < 1) { scale *= cap; w = Math.Max(1, (int)(c.Width * scale)); h = Math.Max(1, (int)(c.Height * scale)); }

        int stride = w * 4;
        IntPtr buffer = Marshal.AllocHGlobal(stride * h);
        try
        {
            lock (Gate)
            {
                var page = GetPageHandle(pageIndex);
                var bmp = fpdfview.FPDFBitmapCreateEx(w, h, 4 /*BGRA*/, buffer, stride);
                try
                {
                    fpdfview.FPDFBitmapFillRect(bmp, 0, 0, w, h, 0xFFFFFFFFUL);
                    int fullW = (int)Math.Round(size.Width * scale), fullH = (int)Math.Round(size.Height * scale);
                    fpdfview.FPDF_RenderPageBitmap(bmp, page, -(int)Math.Round(c.X * scale), -(int)Math.Round(c.Y * scale),
                        fullW, fullH, 0, (RenderAnnotations ? FPDF_ANNOT : 0));
                }
                finally { fpdfview.FPDFBitmapDestroy(bmp); }
            }
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buffer, stride * h, stride);
            src.Freeze();
            _bitmapCache.Add(key, src);
            return src;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public Task<BitmapSource> RenderAsync(int pageIndex, double scale, Rect? clip, CancellationToken ct)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var r = Render(pageIndex, scale, clip);
            ct.ThrowIfCancellationRequested();
            return r;
        }, ct);

    /// <summary>Renders the page to a grayscale buffer (compare engine).</summary>
    public (byte[] Gray, int W, int H) RenderGray(int pageIndex, double scale)
    {
        var bmp = Render(pageIndex, scale);
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        var g = new byte[w * h];
        for (int i = 0, j = 0; i < g.Length; i++, j += 4)
            g[i] = (byte)((px[j] * 29 + px[j + 1] * 150 + px[j + 2] * 77) >> 8);
        return (g, w, h);
    }

    /// <summary>Thumbnail with a max side length in pixels.</summary>
    public BitmapSource RenderThumbnail(int pageIndex, int maxSide)
    {
        var s = GetPageSize(pageIndex);
        double scale = maxSide / Math.Max(s.Width, s.Height);
        return Render(pageIndex, scale);
    }

    // ------------------------------------------------------------------ text
    /// <summary>Words of a page in display coordinates (cached).</summary>
    public IReadOnlyList<TextWord> GetWords(int pageIndex)
    {
        lock (Gate)
        {
            if (_textCache.TryGetValue(pageIndex, out var cached)) return cached;
            var page = GetPageHandle(pageIndex);
            var aff = _toDisplay[pageIndex]!.Value;
            var words = new List<TextWord>();
            var tp = fpdf_text.FPDFTextLoadPage(page);
            if (tp != null && tp.__Instance != IntPtr.Zero)
            {
                try
                {
                    int n = fpdf_text.FPDFTextCountChars(tp);
                    var sb = new StringBuilder();
                    var glyphs = new List<TextGlyph>();
                    PdfRect? box = null;
                    double lastRight = double.NaN, lastH = 0;
                    void Flush()
                    {
                        if (sb.Length > 0 && box != null) words.Add(new TextWord(sb.ToString(), box.Value, glyphs.ToArray()));
                        sb.Clear(); glyphs.Clear(); box = null; lastRight = double.NaN;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        uint u = fpdf_text.FPDFTextGetUnicode(tp, i);
                        if (u == 0 || u == 32 || u == 9 || u == 10 || u == 13 || u == 0xA0 || u == 0xFFFE) { Flush(); continue; }
                        double l = 0, r = 0, b = 0, t = 0;
                        if (fpdf_text.FPDFTextGetCharBox(tp, i, ref l, ref r, ref b, ref t) == 0) continue;
                        var p1 = aff.Apply(l, t); var p2 = aff.Apply(r, b);
                        var cr = PdfRect.FromPoints(p1, p2);
                        // Break words on large gaps (PDFs without explicit spaces).
                        if (box != null && !double.IsNaN(lastRight))
                        {
                            double gap = Math.Max(cr.X - box.Value.Right, box.Value.X - cr.Right);
                            if (gap > Math.Max(lastH, cr.Height) * 0.8 || Math.Abs(cr.Y - box.Value.Y) > Math.Max(lastH, cr.Height) * 1.5) Flush();
                        }
                        string s = u <= 0xFFFF ? ((char)u).ToString() : char.ConvertFromUtf32((int)u);
                        sb.Append(s);
                        glyphs.Add(new TextGlyph(s, cr));
                        box = box == null ? cr : box.Value.Union(cr);
                        lastRight = cr.Right; lastH = cr.Height;
                    }
                    Flush();
                }
                finally { fpdf_text.FPDFTextClosePage(tp); }
            }
            _textCache[pageIndex] = words;
            return words;
        }
    }

    /// <summary>Text inside a display rectangle, ordered in reading lines.
    /// Words are clipped per character, so a rectangle covering only the tail of a long word
    /// (e.g. "52221.D" of "MEL03B-RIN-DR-ST-52221.D") returns just that part.</summary>
    public string GetTextInRect(int pageIndex, PdfRect rect, IReadOnlyList<TextWord>? extra = null)
    {
        var words = new List<TextWord>();
        foreach (var w in GetWords(pageIndex).Concat(extra ?? Array.Empty<TextWord>()))
        {
            var clipped = ClipWord(w, rect);
            if (clipped != null) words.Add(clipped);
        }
        return TextTools.JoinLines(words);
    }

    /// <summary>Returns the part of a word whose characters have their centre inside the rectangle (null if none).</summary>
    private static TextWord? ClipWord(TextWord w, PdfRect rect)
    {
        if (!w.Rect.IntersectsWith(rect)) return null;
        // Whole word inside -> keep as is.
        if (rect.Contains(w.Rect.X, w.Rect.Y) && rect.Contains(w.Rect.Right, w.Rect.Bottom)) return w;

        if (w.Glyphs is { Count: > 0 } gl)
        {
            var sb = new StringBuilder();
            PdfRect? box = null;
            foreach (var g in gl)
            {
                if (!rect.Contains(g.Rect.X + g.Rect.Width / 2, g.Rect.Y + g.Rect.Height / 2)) continue;
                sb.Append(g.Text);
                box = box == null ? g.Rect : box.Value.Union(g.Rect);
            }
            return box == null ? null : new TextWord(sb.ToString(), box.Value);
        }

        // OCR word without glyph boxes: vertical centre must be inside, then estimate characters
        // as evenly spaced across the word width.
        double cy = w.Rect.Y + w.Rect.Height / 2;
        if (cy < rect.Y || cy > rect.Bottom || w.Text.Length == 0) return null;
        double cw = w.Rect.Width / w.Text.Length;
        int first = -1, last = -1;
        for (int i = 0; i < w.Text.Length; i++)
        {
            double cx = w.Rect.X + (i + 0.5) * cw;
            if (cx < rect.X || cx > rect.Right) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return null;
        return new TextWord(w.Text.Substring(first, last - first + 1),
            new PdfRect(w.Rect.X + first * cw, w.Rect.Y, (last - first + 1) * cw, w.Rect.Height));
    }

    /// <summary>Case-insensitive search across all pages. Matches whole words or substrings of words / phrases.</summary>
    public List<SearchHit> Search(string query, CancellationToken ct, IProgress<int>? progress = null,
        Func<int, IReadOnlyList<TextWord>>? extraWords = null)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        string q = query.Trim();
        var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int p = 0; p < PageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            var words = GetWords(p).ToList();
            if (extraWords != null) words.AddRange(extraWords(p));
            for (int i = 0; i < words.Count; i++)
            {
                if (tokens.Length == 1)
                {
                    if (words[i].Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                        hits.Add(new SearchHit(p, words[i].Rect, Context(words, i)));
                }
                else if (i + tokens.Length <= words.Count)
                {
                    bool ok = true;
                    for (int k = 0; k < tokens.Length && ok; k++)
                        ok = words[i + k].Text.Contains(tokens[k], StringComparison.OrdinalIgnoreCase);
                    if (ok)
                    {
                        var r = words[i].Rect;
                        for (int k = 1; k < tokens.Length; k++) r = r.Union(words[i + k].Rect);
                        hits.Add(new SearchHit(p, r, Context(words, i)));
                    }
                }
            }
            progress?.Report(p + 1);
        }
        return hits;
    }

    private static string Context(List<TextWord> words, int i)
    {
        int a = Math.Max(0, i - 3), b = Math.Min(words.Count, i + 4);
        return string.Join(" ", words.Skip(a).Take(b - a).Select(w => w.Text));
    }

    // ------------------------------------------------------------------ outline
    public List<BookmarkItem> GetBookmarks()
    {
        var result = new List<BookmarkItem>();
        try
        {
            lock (Gate)
            {
                if (_doc == null) return result;
                ReadBookmarks(null, result, 0);
            }
        }
        catch (Exception ex) { Logger.Warn("Bookmarks: " + ex.Message); }
        return result;
    }

    private void ReadBookmarks(FpdfBookmarkT? parent, List<BookmarkItem> into, int depth)
    {
        if (depth > 12 || into.Count > 5000) return;
        var bm = fpdf_doc.FPDFBookmarkGetFirstChild(_doc!, parent!);
        while (bm != null && bm.__Instance != IntPtr.Zero)
        {
            var item = new BookmarkItem();
            ulong len = fpdf_doc.FPDFBookmarkGetTitle(bm, IntPtr.Zero, 0);
            if (len > 2)
            {
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try { fpdf_doc.FPDFBookmarkGetTitle(bm, buf, len); item.Title = Marshal.PtrToStringUni(buf, (int)len / 2 - 1) ?? ""; }
                finally { Marshal.FreeHGlobal(buf); }
            }
            var dest = fpdf_doc.FPDFBookmarkGetDest(_doc!, bm);
            if (dest != null && dest.__Instance != IntPtr.Zero) item.Page = fpdf_doc.FPDFDestGetDestPageIndex(_doc!, dest);
            else
            {
                var action = fpdf_doc.FPDFBookmarkGetAction(bm);
                if (action != null && action.__Instance != IntPtr.Zero)
                {
                    var d2 = fpdf_doc.FPDFActionGetDest(_doc!, action);
                    if (d2 != null && d2.__Instance != IntPtr.Zero) item.Page = fpdf_doc.FPDFDestGetDestPageIndex(_doc!, d2);
                }
            }
            into.Add(item);
            ReadBookmarks(bm, item.Children, depth + 1);
            bm = fpdf_doc.FPDFBookmarkGetNextSibling(_doc!, bm);
        }
    }

    // ------------------------------------------------------------------ dispose
    public void Dispose()
    {
        lock (Gate)
        {
            foreach (var p in _openPages.Values) fpdfview.FPDF_ClosePage(p);
            _openPages.Clear(); _pageLru.Clear();
            if (_doc != null) { fpdfview.FPDF_CloseDocument(_doc); _doc = null; }
        }
        _bitmapCache.Clear();
        if (_tempCopy != null) TryDelete(_tempCopy);
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { /* ignore */ } }
}

/// <summary>Small thread-safe LRU cache (page bitmaps / thumbnails).</summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _list = new();
    private readonly object _lock = new();
    public LruCache(int capacity) => _capacity = capacity;

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node); _list.AddFirst(node);
                value = node.Value.Value; return true;
            }
            value = default!; return false;
        }
    }

    public void Add(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var old)) { _list.Remove(old); _map.Remove(key); }
            var node = _list.AddFirst((key, value));
            _map[key] = node;
            while (_map.Count > _capacity) { var last = _list.Last!; _list.RemoveLast(); _map.Remove(last.Value.Key); }
        }
    }

    public void Clear() { lock (_lock) { _map.Clear(); _list.Clear(); } }
}

/// <summary>Text helpers shared by region read / zones / compare.</summary>
public static class TextTools
{
    /// <summary>Groups words into lines (by Y) and joins them in reading order.</summary>
    public static string JoinLines(IEnumerable<TextWord> words)
    {
        var list = words.OrderBy(w => w.Rect.Y).ToList();
        var lines = new List<List<TextWord>>();
        foreach (var w in list)
        {
            var line = lines.LastOrDefault();
            if (line != null && Math.Abs(line[0].Rect.Y + line[0].Rect.Height / 2 - (w.Rect.Y + w.Rect.Height / 2)) < Math.Max(2, line[0].Rect.Height * 0.5))
                line.Add(w);
            else lines.Add(new List<TextWord> { w });
        }
        return string.Join(Environment.NewLine, lines.Select(l => string.Join(" ", l.OrderBy(w => w.Rect.X).Select(w => w.Text))));
    }
}
