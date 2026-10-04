// ============================================================================
//  Services.cs  –  BUSINESS LOGIC (no UI)
//  Logger, AppPaths, Geometry, MeasurementService (scale/length/area...),
//  TakeoffService, DatabaseService (SQLite), ProjectService (save/load/
//  autosave/backup/crash recovery), UndoService, CompareService,
//  OverlayService, ZoneService (region QA), OcrService (Windows OCR).
// ============================================================================
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace JnnPdf;

// ----------------------------------------------------------------------------
//  Paths + logging
// ----------------------------------------------------------------------------
public static class AppPaths
{
    public static string Root { get; } = Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JNNPDF"));
    public static string Logs { get; } = Ensure(Path.Combine(Root, "logs"));
    public static string Autosave { get; } = Ensure(Path.Combine(Root, "autosave"));
    public static string Database => Path.Combine(Root, "jnnpdf.db");
    public static string SessionFile => Path.Combine(Root, "session.lock");
    private static string Ensure(string p) { Directory.CreateDirectory(p); return p; }
}

/// <summary>Very small thread-safe file logger.</summary>
public static class Logger
{
    private static readonly object Lock = new();
    private static string FilePath => Path.Combine(AppPaths.Logs, $"jnnpdf_{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}\n{ex}");

    private static void Write(string level, string msg)
    {
        try { lock (Lock) File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}"); }
        catch { /* logging must never crash the app */ }
    }
}

// ----------------------------------------------------------------------------
//  Geometry helpers
// ----------------------------------------------------------------------------
public static class Geometry
{
    public static double Dist(PdfPoint a, PdfPoint b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    public static double PolyLength(IReadOnlyList<PdfPoint> pts, bool closed)
    {
        double s = 0;
        for (int i = 1; i < pts.Count; i++) s += Dist(pts[i - 1], pts[i]);
        if (closed && pts.Count > 2) s += Dist(pts[^1], pts[0]);
        return s;
    }

    /// <summary>Shoelace polygon area (absolute).</summary>
    public static double PolygonArea(IReadOnlyList<PdfPoint> pts)
    {
        double a = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i]; var q = pts[(i + 1) % pts.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return Math.Abs(a) / 2;
    }

    /// <summary>Angle at vertex B in degrees for A-B-C.</summary>
    public static double AngleDeg(PdfPoint a, PdfPoint b, PdfPoint c)
    {
        double a1 = Math.Atan2(a.Y - b.Y, a.X - b.X), a2 = Math.Atan2(c.Y - b.Y, c.X - b.X);
        double d = Math.Abs(a1 - a2) * 180 / Math.PI;
        return d > 180 ? 360 - d : d;
    }

    public static double DistToSegment(PdfPoint p, PdfPoint a, PdfPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return Dist(p, a);
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return Dist(p, new PdfPoint(a.X + t * dx, a.Y + t * dy));
    }

    public static bool PointInPolygon(PdfPoint p, IReadOnlyList<PdfPoint> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y + 1e-12) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }

    public static PdfRect BoundsOf(Markup m)
    {
        var pts = m.Points;
        PdfRect? r = null;
        if (pts.Count > 0)
        {
            double x0 = pts.Min(p => p.X), y0 = pts.Min(p => p.Y), x1 = pts.Max(p => p.X), y1 = pts.Max(p => p.Y);
            r = new PdfRect(x0, y0, x1 - x0, y1 - y0);
        }
        if (m.Rects != null)
            foreach (var rc in m.Rects) r = r == null ? rc : r.Value.Union(rc);
        return r ?? new PdfRect(0, 0, 0, 0);
    }

    /// <summary>True for types whose geometry is a 2-point box.</summary>
    public static bool IsBoxType(MarkupType t) => t is MarkupType.TextBox or MarkupType.Typewriter or MarkupType.Label
        or MarkupType.Rectangle or MarkupType.Ellipse or MarkupType.Cloud or MarkupType.Highlight or MarkupType.Tag
        or MarkupType.Stamp or MarkupType.Hyperlink;

    /// <summary>Generates a revision-cloud outline (sequence of arc points) around a polygon.</summary>
    public static List<PdfPoint> CloudPoints(IReadOnlyList<PdfPoint> poly, double arc)
    {
        var result = new List<PdfPoint>();
        if (poly.Count < 2) return result;
        arc = Math.Max(arc, 2);
        // Ensure consistent orientation so bumps point outward (signed area > 0 => clockwise in y-down space).
        var pts = poly.ToList();
        double signed = 0;
        for (int i = 0; i < pts.Count; i++) { var p = pts[i]; var q = pts[(i + 1) % pts.Count]; signed += p.X * q.Y - q.X * p.Y; }
        if (signed < 0) pts.Reverse();
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            double len = Dist(a, b);
            int n = Math.Max(1, (int)Math.Round(len / arc));
            for (int k = 0; k < n; k++)
            {
                var s = new PdfPoint(a.X + (b.X - a.X) * k / n, a.Y + (b.Y - a.Y) * k / n);
                var e = new PdfPoint(a.X + (b.X - a.X) * (k + 1) / n, a.Y + (b.Y - a.Y) * (k + 1) / n);
                var c = new PdfPoint((s.X + e.X) / 2, (s.Y + e.Y) / 2);
                double r = Dist(s, e) / 2;
                double start = Math.Atan2(s.Y - c.Y, s.X - c.X);
                // half circle bulging outward (to the left of travel in y-down clockwise polygon)
                for (int j = 0; j <= 8; j++)
                {
                    double ang = start + Math.PI * j / 8;
                    result.Add(new PdfPoint(c.X + r * Math.Cos(ang), c.Y + r * Math.Sin(ang)));
                }
            }
        }
        return result;
    }

    public static List<PdfPoint> RectCorners(PdfRect r) => new()
    {
        new(r.X, r.Y), new(r.Right, r.Y), new(r.Right, r.Bottom), new(r.X, r.Bottom)
    };
}

// ----------------------------------------------------------------------------
//  Measurement + scale
// ----------------------------------------------------------------------------
public static class MeasurementService
{
    private static double PaperUnitPerPoint(string unit) => unit switch { "mm" => 25.4 / 72, "in" => 1.0 / 72, _ => 1.0 };
    public static double MetersPerUnit(string unit) => unit switch { "m" => 1, "cm" => 0.01, "mm" => 0.001, "ft" => 0.3048, "in" => 0.0254, _ => 1 };

    /// <summary>Real distance (display unit) represented by 1 PDF point.</summary>
    public static double RealPerPoint(ScaleSetting s)
    {
        if (s.PaperValue <= 0 || s.RealValue <= 0) throw new ArgumentException("Invalid scale (values must be > 0).");
        double meters = PaperUnitPerPoint(s.PaperUnit) / s.PaperValue * s.RealValue * MetersPerUnit(s.RealUnit);
        return meters / MetersPerUnit(s.DisplayUnit);
    }

    /// <summary>Builds a scale from a calibration line: pdfLengthPt points = realLength [unit].</summary>
    public static ScaleSetting Calibrate(double pdfLengthPt, double realLength, string realUnit)
    {
        if (pdfLengthPt <= 0 || realLength <= 0) throw new ArgumentException("Calibration values must be > 0.");
        return new ScaleSetting
        {
            PaperValue = Math.Round(pdfLengthPt * 25.4 / 72, 4), PaperUnit = "mm",
            RealValue = realLength, RealUnit = realUnit, DisplayUnit = realUnit == "mm" || realUnit == "cm" ? "m" : realUnit
        };
    }

    /// <summary>Parses "1:100", "1/50" or a plain number "200".</summary>
    public static bool TryParseRatio(string text, out ScaleSetting scale)
    {
        scale = ScaleSetting.Ratio(100);
        text = (text ?? "").Trim().Replace(" ", "");
        var m = Regex.Match(text, @"^(\d+(?:\.\d+)?)[:/](\d+(?:\.\d+)?)$");
        if (m.Success)
        {
            double a = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), b = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (a <= 0 || b <= 0) return false;
            scale = new ScaleSetting { PaperValue = a, PaperUnit = "mm", RealValue = b, RealUnit = "mm", DisplayUnit = "m" };
            return true;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0)
        {
            scale = ScaleSetting.Ratio(d); return true;
        }
        return false;
    }

    /// <summary>Computes and stores Value / ValueText for a measurement markup.</summary>
    public static void Update(Markup m, ScaleSetting scale)
    {
        if (!m.IsMeasurement) { m.ValueText = ""; return; }
        double k;
        try { k = RealPerPoint(scale); } catch { k = 1; }
        string u = scale.DisplayUnit;
        var p = m.Points;
        switch (m.Type)
        {
            case MarkupType.Length:
                m.Value = p.Count >= 2 ? Geometry.Dist(p[0], p[1]) * k : 0;
                m.ValueText = $"Length = {m.Value:N2} {u}"; break;
            case MarkupType.PolyLength:
                m.Value = Geometry.PolyLength(p, false) * k;
                m.ValueText = $"PolyLength = {m.Value:N2} {u}"; break;
            case MarkupType.Perimeter:
                m.Value = Geometry.PolyLength(p, true) * k;
                m.ValueText = $"Perimeter = {m.Value:N2} {u}"; break;
            case MarkupType.Area:
                m.Value = p.Count >= 3 ? Geometry.PolygonArea(p) * k * k : 0;
                m.ValueText = m.Depth > 0
                    ? $"Area = {m.Value:N2} {u}²  |  Vol = {m.Value * m.Depth:N2} {u}³"
                    : $"Area = {m.Value:N2} {u}²"; break;
            case MarkupType.Count:
                m.Value = p.Count;
                m.ValueText = $"Count = {p.Count}"; break;
            case MarkupType.Angle:
                m.Value = p.Count >= 3 ? Geometry.AngleDeg(p[0], p[1], p[2]) : 0;
                m.ValueText = $"Angle = {m.Value:0.0}°"; break;
        }
    }
}

// ----------------------------------------------------------------------------
//  Takeoff
// ----------------------------------------------------------------------------
public static class TakeoffService
{
    /// <summary>Aggregates measurement markups that have a takeoff Category.</summary>
    public static List<TakeoffSummary> Summarize(IEnumerable<Markup> markups, Func<int, ScaleSetting> scaleOf)
    {
        var rows = new List<(Markup M, string Type, double Q, string Unit)>();
        foreach (var m in markups.Where(m => m.IsMeasurement && !string.IsNullOrWhiteSpace(m.Category)))
        {
            MeasurementService.Update(m, scaleOf(m.Page));
            string u = scaleOf(m.Page).DisplayUnit;
            switch (m.Type)
            {
                case MarkupType.Area when m.Depth > 0: rows.Add((m, "Volume", m.Value * m.Depth, u + "³")); break;
                case MarkupType.Area: rows.Add((m, "Area", m.Value, u + "²")); break;
                case MarkupType.Count: rows.Add((m, "Count", m.Value, "ea")); break;
                case MarkupType.Angle: break;
                default: rows.Add((m, "Length", m.Value, u)); break;
            }
        }
        return rows.GroupBy(r => (r.M.Category, Name: string.IsNullOrWhiteSpace(r.M.TakeoffName) ? r.M.Subject : r.M.TakeoffName, r.Type, r.Unit))
            .Select(g => new TakeoffSummary(g.Key.Category, g.Key.Name, g.Key.Type, Math.Round(g.Sum(r => r.Q), 3), g.Key.Unit,
                g.Count(), string.Join(",", g.Select(r => r.M.PageNumber).Distinct().OrderBy(x => x)), g.First().M.Color))
            .OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();
    }
}

// ----------------------------------------------------------------------------
//  SQLite: recent files + settings
// ----------------------------------------------------------------------------
public sealed class DatabaseService
{
    private readonly string _cs;

    public DatabaseService()
    {
        _cs = new SqliteConnectionStringBuilder { DataSource = AppPaths.Database }.ToString();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS recent(path TEXT PRIMARY KEY, kind TEXT, opened TEXT);
                                CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Logger.Error("DB init failed", ex); }
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_cs); c.Open(); return c; }

    public void AddRecent(string path, string kind)
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO recent(path, kind, opened) VALUES($p, $k, $d)";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$d", DateTime.Now.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Logger.Error("AddRecent", ex); }
    }

    public List<string> GetRecent(int max = 12)
    {
        var list = new List<string>();
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT path FROM recent ORDER BY opened DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", max);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
        }
        catch (Exception ex) { Logger.Error("GetRecent", ex); }
        return list;
    }

    public void ClearRecent()
    {
        try { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM recent"; cmd.ExecuteNonQuery(); }
        catch (Exception ex) { Logger.Error("ClearRecent", ex); }
    }

    public string Get(string key, string def = "")
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string ?? def;
        }
        catch { return def; }
    }

    public void Set(string key, string value)
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO settings(key, value) VALUES($k, $v)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Logger.Error("Settings.Set", ex); }
    }
}

// ----------------------------------------------------------------------------
//  Project save / load / autosave / backup / recovery
// ----------------------------------------------------------------------------
public static class ProjectService
{
    public const string Extension = ".jnnreview";
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static string Serialize(ProjectData p) => JsonSerializer.Serialize(p, Json);
    public static ProjectData Deserialize(string json) => JsonSerializer.Deserialize<ProjectData>(json, Json) ?? new ProjectData();

    /// <summary>Saves atomically (write temp file then replace).</summary>
    public static void Save(string path, ProjectData p)
    {
        try
        {
            p.Modified = DateTime.Now;
            if (!string.IsNullOrEmpty(p.PdfPath))
            {
                try { p.PdfRelativePath = Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(path))!, p.PdfPath); }
                catch { p.PdfRelativePath = ""; }
            }
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(p), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch (UnauthorizedAccessException)
        {
            throw new IOException($"No permission to write the file:\n{path}");
        }
    }

    /// <summary>Loads a project and resolves the PDF path (relative first, absolute fallback).</summary>
    public static ProjectData Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Project file not found.", path);
        var p = Deserialize(File.ReadAllText(path, Encoding.UTF8));
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!string.IsNullOrEmpty(p.PdfRelativePath))
        {
            string candidate = Path.GetFullPath(Path.Combine(dir, p.PdfRelativePath));
            if (File.Exists(candidate)) p.PdfPath = candidate;
        }
        return p;
    }

    /// <summary>Autosave target when the project has never been saved.</summary>
    public static string AutosavePathFor(string? projectPath, string? pdfPath)
    {
        if (!string.IsNullOrEmpty(projectPath)) return projectPath;
        string name = string.IsNullOrEmpty(pdfPath) ? "untitled" : Path.GetFileNameWithoutExtension(pdfPath);
        return Path.Combine(AppPaths.Autosave, name + Extension);
    }

    /// <summary>Writes rolling backups Project_backup_001..003 (001 = newest).</summary>
    public static string WriteBackup(string projectPath, ProjectData p)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        string baseName = Path.GetFileNameWithoutExtension(projectPath);
        string B(int i) => Path.Combine(dir, $"{baseName}_backup_{i:000}{Extension}");
        try
        {
            if (File.Exists(B(3))) File.Delete(B(3));
            if (File.Exists(B(2))) File.Move(B(2), B(3));
            if (File.Exists(B(1))) File.Move(B(1), B(2));
            File.WriteAllText(B(1), Serialize(p), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // Fall back to the app autosave folder (read-only project folder...).
            Logger.Warn("Backup in project folder failed: " + ex.Message);
            string alt = Path.Combine(AppPaths.Autosave, $"{baseName}_backup_001{Extension}");
            File.WriteAllText(alt, Serialize(p), Encoding.UTF8);
            return alt;
        }
        return B(1);
    }

    // ---- crash recovery: a session marker exists while the app is running ----
    public static void MarkSession(string? lastBackup, string? projectPath)
        => SafeWrite(AppPaths.SessionFile, JsonSerializer.Serialize(new[] { lastBackup ?? "", projectPath ?? "" }));

    /// <summary>Returns (backup, original project) if the previous session did not exit cleanly.</summary>
    public static (string Backup, string? Project)? GetCrashedSession()
    {
        try
        {
            if (!File.Exists(AppPaths.SessionFile)) return null;
            var arr = JsonSerializer.Deserialize<string[]>(File.ReadAllText(AppPaths.SessionFile));
            if (arr == null || arr.Length == 0 || !File.Exists(arr[0])) return null;
            return (arr[0], arr.Length > 1 && arr[1].Length > 0 ? arr[1] : null);
        }
        catch { return null; }
    }

    public static void EndSession() { try { File.Delete(AppPaths.SessionFile); } catch { } }

    private static void SafeWrite(string p, string s) { try { File.WriteAllText(p, s); } catch { } }
}

// ----------------------------------------------------------------------------
//  Undo / Redo using JSON snapshots of the editable state
// ----------------------------------------------------------------------------
public sealed class UndoService
{
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private const int Max = 100;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Pushes the state BEFORE a change.</summary>
    public void Push(string snapshot)
    {
        if (_undo.Count > 0 && _undo.Peek() == snapshot) return;
        _undo.Push(snapshot);
        _redo.Clear();
        if (_undo.Count > Max)
        {
            var keep = _undo.Take(Max).Reverse().ToList();
            _undo.Clear();
            foreach (var s in keep) _undo.Push(s);
        }
    }

    public string? Undo(string current) { if (!CanUndo) return null; _redo.Push(current); return _undo.Pop(); }
    public string? Redo(string current) { if (!CanRedo) return null; _undo.Push(current); return _redo.Pop(); }
    public void Clear() { _undo.Clear(); _redo.Clear(); }
}

// ----------------------------------------------------------------------------
//  Compare revisions: pixel diff (added / removed / changed) + text diff
// ----------------------------------------------------------------------------
public static class CompareService
{
    /// <summary>Compares page <paramref name="pa"/> of A with page <paramref name="pb"/> of B.
    /// Returns regions in A's display coordinates.</summary>
    public static List<ChangeRegion> ComparePage(PdfDocument a, int pa, PdfDocument b, int pb, CancellationToken ct, int targetPx = 2400)
    {
        var result = new List<ChangeRegion>();
        var sa = a.GetPageSize(pa); var sb = b.GetPageSize(pb);
        double scaleA = targetPx / Math.Max(sa.Width, sa.Height);
        var (ga, w, h) = a.RenderGray(pa, scaleA);
        ct.ThrowIfCancellationRequested();
        // Render B at the same pixel size (align by page).
        double scaleB = scaleA * sa.Width / Math.Max(1, sb.Width);
        var (gb0, wb, hb) = b.RenderGray(pb, scaleB);
        ct.ThrowIfCancellationRequested();
        var gb = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                gb[y * w + x] = (x < wb && y < hb) ? gb0[y * wb + x] : (byte)255;

        const byte ink = 200;
        bool[] ia = new bool[w * h], ib = new bool[w * h];
        for (int i = 0; i < ia.Length; i++) { ia[i] = ga[i] < ink; ib[i] = gb[i] < ink; }
        bool[] da = Dilate(ia, w, h), db = Dilate(ib, w, h);

        const int cell = 8;
        int cw = (w + cell - 1) / cell, ch = (h + cell - 1) / cell;
        int[] rem = new int[cw * ch], add = new int[cw * ch];
        for (int y = 0; y < h; y++)
        {
            int row = y * w, crow = (y / cell) * cw;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                if (ia[i] && !db[i]) rem[crow + x / cell]++;
                else if (ib[i] && !da[i]) add[crow + x / cell]++;
            }
        }
        ct.ThrowIfCancellationRequested();
        bool[] flag = new bool[cw * ch];
        for (int i = 0; i < flag.Length; i++) flag[i] = rem[i] + add[i] >= 4;

        // Connected components with a 2-cell merge gap.
        var seen = new bool[cw * ch];
        var stack = new Stack<int>();
        const int gap = 2;
        for (int start = 0; start < flag.Length; start++)
        {
            if (!flag[start] || seen[start]) continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, nRem = 0, nAdd = 0;
            stack.Push(start); seen[start] = true;
            while (stack.Count > 0)
            {
                int c = stack.Pop(); int cx = c % cw, cy = c / cw;
                minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx); minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                nRem += rem[c]; nAdd += add[c];
                for (int dy = -gap; dy <= gap; dy++)
                    for (int dx = -gap; dx <= gap; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                        int n = ny * cw + nx;
                        if (flag[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                    }
            }
            var rect = new PdfRect(minX * cell / scaleA, minY * cell / scaleA, (maxX - minX + 1) * cell / scaleA, (maxY - minY + 1) * cell / scaleA).Inflate(3);
            string kind = nAdd > nRem * 4 ? "Added" : nRem > nAdd * 4 ? "Removed" : "Changed";
            result.Add(new ChangeRegion(pa, rect, kind, $"{kind} drawing area ({nAdd} px added / {nRem} px removed)"));
            if (result.Count > 400) break;
        }

        // Text diff (position-aware).
        try
        {
            var wa = a.GetWords(pa); var wbw = b.GetWords(pb);
            double fx = sa.Width / Math.Max(1, sb.Width);
            var bList = wbw.Select(t => new TextWord(t.Text, new PdfRect(t.Rect.X * fx, t.Rect.Y * fx, t.Rect.Width * fx, t.Rect.Height * fx))).ToList();
            var usedB = new bool[bList.Count];
            foreach (var t in wa)
            {
                int best = -1; double bestD = 4;
                for (int j = 0; j < bList.Count; j++)
                {
                    if (usedB[j]) continue;
                    double d = Math.Abs(bList[j].Rect.X - t.Rect.X) + Math.Abs(bList[j].Rect.Y - t.Rect.Y);
                    if (d < bestD) { bestD = d; best = j; }
                }
                if (best < 0) { result.Add(new ChangeRegion(pa, t.Rect.Inflate(2), "Text removed", $"Text removed: {t.Text}")); continue; }
                usedB[best] = true;
                if (!string.Equals(bList[best].Text, t.Text, StringComparison.Ordinal))
                    result.Add(new ChangeRegion(pa, t.Rect.Union(bList[best].Rect).Inflate(2), "Text changed", $"Text changed: {t.Text} → {bList[best].Text}"));
            }
            for (int j = 0; j < bList.Count; j++)
                if (!usedB[j]) result.Add(new ChangeRegion(pa, bList[j].Rect.Inflate(2), "Text added", $"Text added: {bList[j].Text}"));
        }
        catch (Exception ex) { Logger.Warn("Text diff failed: " + ex.Message); }
        return result.OrderBy(r => r.Rect.Y).ThenBy(r => r.Rect.X).ToList();
    }

    private static bool[] Dilate(bool[] src, int w, int h)
    {
        var dst = new bool[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!src[y * w + x]) continue;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy; if (ny < 0 || ny >= h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx; if (nx < 0 || nx >= w) continue;
                        dst[ny * w + nx] = true;
                    }
                }
            }
        return dst;
    }
}

// ----------------------------------------------------------------------------
//  Overlay helpers: tint a rendered page (ink -> colour, paper -> transparent)
// ----------------------------------------------------------------------------
public static class OverlayService
{
    public static BitmapSource Tint(BitmapSource src, Color color)
    {
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        var conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        conv.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            int lum = (px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8;
            byte alpha = (byte)(255 - lum);
            // premultiplied colour for Pbgra32
            px[i] = (byte)(color.B * alpha / 255);
            px[i + 1] = (byte)(color.G * alpha / 255);
            px[i + 2] = (byte)(color.R * alpha / 255);
            px[i + 3] = alpha;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }
}

// ----------------------------------------------------------------------------
//  Zone check (region QA rules) – same logic as the Python prototype
// ----------------------------------------------------------------------------
public static class ZoneService
{
    public static (bool Passed, string Message) Evaluate(Zone z, string value, string? valueB)
    {
        string v = Normalize(value);
        switch (z.Rule)
        {
            case "compare":
                if (valueB == null) return (true, "No PDF 2 – extracted only");
                return Normalize(valueB) == v ? (true, "Identical") : (false, "Different between PDF 1 and PDF 2");
            case "extract":
                return (true, string.IsNullOrEmpty(v) ? "(empty)" : "OK");
            case "not_empty":
                return string.IsNullOrEmpty(v) ? (false, "Zone is empty") : (true, "Has data");
            case "contains":
                return v.Contains(Normalize(z.Expected), StringComparison.OrdinalIgnoreCase) ? (true, "Contains") : (false, $"Does not contain '{z.Expected}'");
            case "equals":
                return string.Equals(v, Normalize(z.Expected), StringComparison.OrdinalIgnoreCase) ? (true, "Matches") : (false, $"Differs from '{z.Expected}'");
            case "regex":
                try { return Regex.IsMatch(value, z.Expected) ? (true, "Regex match") : (false, "No regex match"); }
                catch (ArgumentException ex) { return (false, "Invalid regex: " + ex.Message); }
            case "number_range":
                var m = Regex.Match(z.Expected ?? "", @"(-?\d+(?:[.,]\d+)?)\s*(?:\.\.|-|~|;)\s*(-?\d+(?:[.,]\d+)?)");
                if (!m.Success) return (false, "Expected must be min..max");
                double lo = ParseNum(m.Groups[1].Value), hi = ParseNum(m.Groups[2].Value);
                var nums = Regex.Matches(value, @"-?\d+(?:[.,]\d+)?").Select(x => ParseNum(x.Value)).ToList();
                if (nums.Count == 0) return (false, "No numbers");
                var bad = nums.Where(n => n < lo || n > hi).ToList();
                return bad.Count == 0 ? (true, $"{nums.Count} number(s) in [{lo}..{hi}]") : (false, $"Out of range: {string.Join(", ", bad)}");
        }
        return (true, "");
    }

    private static double ParseNum(string s) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    public static string Normalize(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    /// <summary>Runs all zones on one document (optionally comparing with B).</summary>
    public static List<ZoneResult> Run(IEnumerable<Zone> zones, PdfDocument a, PdfDocument? b, string fileLabel,
        CancellationToken ct, Func<int, IReadOnlyList<TextWord>>? extraA = null)
    {
        var results = new List<ZoneResult>();
        foreach (var z in zones)
        {
            var pages = z.AllPages ? Enumerable.Range(0, a.PageCount) : new[] { z.Page }.Where(p => p < a.PageCount);
            foreach (int p in pages)
            {
                ct.ThrowIfCancellationRequested();
                string va = a.GetTextInRect(p, z.Rect, extraA?.Invoke(p));
                string? vb = b != null && p < b.PageCount ? b.GetTextInRect(p, z.Rect) : null;
                var (ok, msg) = Evaluate(z, va, vb);
                results.Add(new ZoneResult(fileLabel, p, z.Name, z.Rule, va.Replace(Environment.NewLine, " | "), vb?.Replace(Environment.NewLine, " | ") ?? "", ok, msg));
            }
        }
        return results;
    }

    /// <summary>Sheet number / revision heuristics for the drawing register.</summary>
    public static DrawingSheet DetectSheet(PdfDocument doc, int page, IEnumerable<Zone> zones)
    {
        var d = new DrawingSheet { Page = page, Date = DateTime.Today.ToString("dd/MM/yy") };
        foreach (var z in zones)
        {
            string v = ZoneService.Normalize(doc.GetTextInRect(page, z.Rect));
            string n = z.Name.ToLowerInvariant();
            if (n.Contains("sheet") || n.Contains("số bản vẽ") || n.Contains("dwg")) d.Sheet = v;
            else if (n.Contains("title") || n.Contains("tên")) d.Title = v;
            else if (n.Contains("rev")) d.Revision = v;
            else if (n.Contains("date") || n.Contains("ngày")) d.Date = v;
        }
        if (string.IsNullOrEmpty(d.Sheet))
        {
            // Heuristic: sheet numbers like S-101, A101, MEL03-L1-PT-001 found in the bottom-right quarter.
            var size = doc.GetPageSize(page);
            var words = doc.GetWords(page).Where(w => w.Rect.X > size.Width * 0.6 && w.Rect.Y > size.Height * 0.6).ToList();
            var cand = words.Select(w => w.Text).Where(t => Regex.IsMatch(t, @"^[A-Z]{1,6}[-_.]?[A-Z0-9]*[-_.]?\d{2,5}[A-Z]?$")).ToList();
            d.Sheet = cand.LastOrDefault() ?? $"P{page + 1:000}";
            var rev = words.Select(w => w.Text).FirstOrDefault(t => Regex.IsMatch(t, @"^(REV|Rev)?[-.]?[A-Z0-9]{1,2}$") && t.Length <= 4 && t.Any(char.IsLetter));
            if (rev != null && string.IsNullOrEmpty(d.Revision)) d.Revision = rev;
        }
        return d;
    }
}

// ----------------------------------------------------------------------------
//  OCR (Windows.Media.Ocr – offline, uses installed OCR language packs)
// ----------------------------------------------------------------------------
public static class OcrService
{
    public static async Task<List<OcrWord>> RecognizePageAsync(PdfDocument doc, int page, CancellationToken ct)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                     ?? throw new InvalidOperationException("No Windows OCR language pack is installed (Settings > Language > English).");
        var size = doc.GetPageSize(page);
        double scale = Math.Min(Windows.Media.Ocr.OcrEngine.MaxImageDimension / Math.Max(size.Width, size.Height), 4.0);
        var bmp = await Task.Run(() => doc.Render(page, scale), ct);
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        ct.ThrowIfCancellationRequested();
        using var sb = new Windows.Graphics.Imaging.SoftwareBitmap(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, w, h, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        sb.CopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(px));
        var res = await engine.RecognizeAsync(sb);
        var words = new List<OcrWord>();
        foreach (var line in res.Lines)
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new OcrWord(word.Text, new PdfRect(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale)));
            }
        return words;
    }
}
