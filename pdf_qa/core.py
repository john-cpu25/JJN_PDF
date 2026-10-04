"""
Core logic for PDF QA Viewer:
  - PdfDoc: rendering + text extraction in *display* coordinates (rotation aware)
  - Pixel diff (overlay), diff region clustering
  - Text diff (removed / added / changed words)
  - Region text extraction, zone check rules
  - Crop export (vector PDF / PNG)

All rectangles exchanged with the UI are in display coordinates (PDF points,
origin top-left of the page *as displayed*, i.e. after /Rotate is applied).
"""
from __future__ import annotations

import difflib
import os
import re
from collections import OrderedDict, defaultdict
from dataclasses import dataclass, field, asdict

import numpy as np
import pymupdf as fitz

# --------------------------------------------------------------------------- #
#  Colours (RGB) used in overlays
# --------------------------------------------------------------------------- #
COL_REMOVED = (235, 64, 52)      # only in Ver1  -> red
COL_ADDED = (32, 170, 90)        # only in Ver2  -> green
COL_COMMON = (150, 156, 168)     # unchanged ink -> grey
COL_BG = (255, 255, 255)


# --------------------------------------------------------------------------- #
#  Document wrapper
# --------------------------------------------------------------------------- #
class PdfDoc:
    def __init__(self, path: str):
        self.path = path
        self.name = os.path.basename(path)
        self.doc = fitz.open(path)
        self._words: dict[int, list] = {}
        self._cache: OrderedDict = OrderedDict()
        self._cache_max = 6

    # -- basic ------------------------------------------------------------- #
    @property
    def page_count(self) -> int:
        return self.doc.page_count

    def page(self, i: int):
        return self.doc[i]

    def page_rect(self, i: int) -> fitz.Rect:
        return self.doc[i].rect

    def has_page(self, i: int) -> bool:
        return 0 <= i < self.doc.page_count

    # -- rendering ---------------------------------------------------------- #
    def _cached(self, key, fn):
        if key in self._cache:
            self._cache.move_to_end(key)
            return self._cache[key]
        val = fn()
        self._cache[key] = val
        if len(self._cache) > self._cache_max:
            self._cache.popitem(last=False)
        return val

    def render(self, i: int, dpi: float, gray: bool = False, clip: fitz.Rect | None = None) -> np.ndarray:
        """Render page (or clip, display coords) -> numpy array (H,W) or (H,W,3)."""
        def _do():
            pg = self.doc[i]
            z = dpi / 72.0
            cs = fitz.csGRAY if gray else fitz.csRGB
            kw = dict(matrix=fitz.Matrix(z, z), colorspace=cs, alpha=False)
            if clip is not None:
                kw["clip"] = clip
            pix = pg.get_pixmap(**kw)
            a = np.frombuffer(pix.samples, dtype=np.uint8)
            a = a.reshape(pix.height, pix.stride)[:, : pix.width * pix.n]
            if pix.n == 1:
                return a.reshape(pix.height, pix.width).copy()
            return a.reshape(pix.height, pix.width, pix.n).copy()

        if clip is not None:
            return _do()
        return self._cached((i, round(dpi, 2), gray), _do)

    # -- text ---------------------------------------------------------------- #
    def words(self, i: int) -> list[tuple]:
        """Words in display coords: (x0, y0, x1, y1, text, block, line, wno)."""
        if i in self._words:
            return self._words[i]
        pg = self.doc[i]
        raw = pg.get_text("words")
        out = []
        m = pg.rotation_matrix if pg.rotation else None
        for w in raw:
            r = fitz.Rect(w[:4])
            if m is not None:
                r = r * m
                r.normalize()
            out.append((r.x0, r.y0, r.x1, r.y1, w[4], w[5], w[6], w[7]))
        self._words[i] = out
        return out

    def words_in(self, i: int, rect: fitz.Rect) -> list[tuple]:
        res = []
        for w in self.words(i):
            cx, cy = (w[0] + w[2]) / 2, (w[1] + w[3]) / 2
            if rect.x0 <= cx <= rect.x1 and rect.y0 <= cy <= rect.y1:
                res.append(w)
        return res

    def lines_in(self, i: int, rect: fitz.Rect) -> list[str]:
        """Text lines inside rect, ordered top->bottom, left->right."""
        return words_to_lines(self.words_in(i, rect))

    def to_unrotated(self, i: int, rect: fitz.Rect) -> fitz.Rect:
        pg = self.doc[i]
        if not pg.rotation:
            return fitz.Rect(rect)
        r = fitz.Rect(rect) * pg.derotation_matrix
        r.normalize()
        return r

    def close(self):
        try:
            self.doc.close()
        except Exception:
            pass


def words_to_lines(words: list[tuple]) -> list[str]:
    if not words:
        return []
    groups: dict[tuple, list] = defaultdict(list)
    for w in words:
        groups[(w[5], w[6])].append(w)
    lines = []
    for ws in groups.values():
        ws.sort(key=lambda w: w[7])
        y = min(w[1] for w in ws)
        x = min(w[0] for w in ws)
        lines.append((y, x, " ".join(w[4] for w in ws)))
    # cluster lines by y with tolerance so that the order is visual
    lines.sort(key=lambda t: (round(t[0] / 3.0), t[1]))
    return [t[2] for t in lines]


# --------------------------------------------------------------------------- #
#  Array helpers
# --------------------------------------------------------------------------- #
def shift_array(a: np.ndarray, dx: int, dy: int, fill=255) -> np.ndarray:
    """Shift image content by (dx, dy) pixels (positive = right/down)."""
    if dx == 0 and dy == 0:
        return a
    out = np.full_like(a, fill)
    h, w = a.shape[:2]
    xs0, xs1 = max(0, -dx), min(w, w - dx)
    ys0, ys1 = max(0, -dy), min(h, h - dy)
    if xs1 <= xs0 or ys1 <= ys0:
        return out
    out[ys0 + dy: ys1 + dy, xs0 + dx: xs1 + dx] = a[ys0:ys1, xs0:xs1]
    return out


def pad_to(a: np.ndarray, h: int, w: int, fill=255) -> np.ndarray:
    if a.shape[0] == h and a.shape[1] == w:
        return a
    shape = (h, w) + a.shape[2:]
    out = np.full(shape, fill, dtype=a.dtype)
    out[: a.shape[0], : a.shape[1]] = a[:h, :w]
    return out


def dilate(m: np.ndarray, r: int) -> np.ndarray:
    if r <= 0:
        return m
    out = m.copy()
    h, w = m.shape
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            if dx == 0 and dy == 0:
                continue
            ys0, ys1 = max(0, dy), min(h, h + dy)
            xs0, xs1 = max(0, dx), min(w, w + dx)
            out[ys0:ys1, xs0:xs1] |= m[ys0 - dy: ys1 - dy, xs0 - dx: xs1 - dx]
    return out


# --------------------------------------------------------------------------- #
#  Pixel diff
# --------------------------------------------------------------------------- #
@dataclass
class PixelDiff:
    removed: np.ndarray   # bool mask, ink only in Ver1
    added: np.ndarray     # bool mask, ink only in Ver2
    ink1: np.ndarray
    ink2: np.ndarray
    dpi: float

    @property
    def n_removed(self) -> int:
        return int(self.removed.sum())

    @property
    def n_added(self) -> int:
        return int(self.added.sum())

    def overlay_rgb(self) -> np.ndarray:
        h, w = self.removed.shape
        img = np.empty((h, w, 3), dtype=np.uint8)
        img[:] = COL_BG
        img[self.ink1 | self.ink2] = COL_COMMON
        img[self.removed] = COL_REMOVED
        img[self.added] = COL_ADDED
        return img


def pixel_diff(g1: np.ndarray, g2: np.ndarray, dpi: float, ink_thr: int = 200,
               tol: int = 1, shift_px: tuple[int, int] = (0, 0)) -> PixelDiff:
    """g1, g2: grayscale arrays. shift_px: shift applied to g2 to align it to g1."""
    h = max(g1.shape[0], g2.shape[0])
    w = max(g1.shape[1], g2.shape[1])
    g1 = pad_to(g1, h, w)
    g2 = pad_to(g2, h, w)
    if shift_px != (0, 0):
        g2 = shift_array(g2, shift_px[0], shift_px[1])
    i1 = g1 < ink_thr
    i2 = g2 < ink_thr
    d1 = dilate(i1, tol)
    d2 = dilate(i2, tol)
    removed = i1 & ~d2
    added = i2 & ~d1
    return PixelDiff(removed, added, i1, i2, dpi)


def mask_regions(mask: np.ndarray, cell: int = 12, min_px: int = 6, merge: int = 2) -> list[tuple]:
    """Cluster a bool mask into bounding boxes (pixel coords x0,y0,x1,y1,count)."""
    h, w = mask.shape
    gh, gw = -(-h // cell), -(-w // cell)
    m = np.zeros((gh * cell, gw * cell), dtype=np.uint8)
    m[:h, :w] = mask
    counts = m.reshape(gh, cell, gw, cell).sum(axis=(1, 3))
    grid = counts >= min_px
    if not grid.any():
        return []
    grown = dilate(grid, merge)
    seen = np.zeros_like(grown)
    regions = []
    ys, xs = np.nonzero(grid)
    for sy, sx in zip(ys, xs):
        if seen[sy, sx]:
            continue
        stack = [(sy, sx)]
        seen[sy, sx] = True
        y0 = y1 = sy
        x0 = x1 = sx
        total = 0
        while stack:
            cy, cx = stack.pop()
            if grid[cy, cx]:
                total += int(counts[cy, cx])
                y0, y1 = min(y0, cy), max(y1, cy)
                x0, x1 = min(x0, cx), max(x1, cx)
            for ny in (cy - 1, cy, cy + 1):
                for nx in (cx - 1, cx, cx + 1):
                    if 0 <= ny < gh and 0 <= nx < gw and grown[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        stack.append((ny, nx))
        regions.append((x0 * cell, y0 * cell, min(w, (x1 + 1) * cell), min(h, (y1 + 1) * cell), total))
    regions.sort(key=lambda r: -r[4])
    return regions


# --------------------------------------------------------------------------- #
#  Text diff
# --------------------------------------------------------------------------- #
@dataclass
class TextDiff:
    removed: list   # words only in v1 (v1 coords)
    added: list     # words only in v2 (v2 coords)
    changed: list   # (w1, w2) pairs at the same place with different text


def text_diff(w1: list, w2: list, offset=(0.0, 0.0), tol: float = 2.5) -> TextDiff:
    """offset: content at V1 (x,y) appears at V2 (x+dx, y+dy)."""
    dx, dy = offset
    idx: dict[str, list[int]] = defaultdict(list)
    for j, w in enumerate(w2):
        idx[w[4]].append(j)
    used = set()
    removed = []
    for w in w1:
        cx, cy = (w[0] + w[2]) / 2 + dx, (w[1] + w[3]) / 2 + dy
        hit = None
        for j in idx.get(w[4], ()):
            if j in used:
                continue
            o = w2[j]
            if abs((o[0] + o[2]) / 2 - cx) <= tol and abs((o[1] + o[3]) / 2 - cy) <= tol:
                hit = j
                break
        if hit is None:
            removed.append(w)
        else:
            used.add(hit)
    added = [w for j, w in enumerate(w2) if j not in used]

    # pair removed/added words occupying the same place -> "changed"
    # (plain float math: creating fitz.Rect objects here is ~30x slower on dense drawings)
    changed = []
    rem_left, add_used = [], set()
    add_boxes = [(b[0], b[1], b[2], b[3]) for b in added]
    for a in removed:
        ax0, ay0, ax1, ay1 = a[0] + dx, a[1] + dy, a[2] + dx, a[3] + dy
        best, best_area = None, 0.0
        for k, (bx0, by0, bx1, by1) in enumerate(add_boxes):
            iw = (ax1 if ax1 < bx1 else bx1) - (ax0 if ax0 > bx0 else bx0)
            if iw <= 0:
                continue
            ih = (ay1 if ay1 < by1 else by1) - (ay0 if ay0 > by0 else by0)
            if ih <= 0 or k in add_used:
                continue
            area = iw * ih
            if area > best_area:
                best, best_area = k, area
        if best is not None:
            add_used.add(best)
            changed.append((a, added[best]))
        else:
            rem_left.append(a)
    add_left = [b for k, b in enumerate(added) if k not in add_used]
    return TextDiff(rem_left, add_left, changed)


def line_diff(lines1: list[str], lines2: list[str]) -> list[tuple[str, str, str]]:
    """Return rows (status, text_v1, text_v2). status: equal|changed|removed|added."""
    rows = []
    sm = difflib.SequenceMatcher(a=lines1, b=lines2, autojunk=False)
    for op, a0, a1, b0, b1 in sm.get_opcodes():
        if op == "equal":
            for k in range(a1 - a0):
                rows.append(("equal", lines1[a0 + k], lines2[b0 + k]))
        elif op == "replace":
            n = max(a1 - a0, b1 - b0)
            for k in range(n):
                t1 = lines1[a0 + k] if a0 + k < a1 else ""
                t2 = lines2[b0 + k] if b0 + k < b1 else ""
                st = "changed" if t1 and t2 else ("removed" if t1 else "added")
                rows.append((st, t1, t2))
        elif op == "delete":
            for k in range(a0, a1):
                rows.append(("removed", lines1[k], ""))
        elif op == "insert":
            for k in range(b0, b1):
                rows.append(("added", "", lines2[k]))
    return rows


# --------------------------------------------------------------------------- #
#  Diff items for a page
# --------------------------------------------------------------------------- #
@dataclass
class DiffItem:
    kind: str            # geo_removed | geo_added | geo_changed | removed | added | changed
    page: int
    rect: tuple          # display coords in V1 space
    text: str = ""

    LABELS = {
        "geo_removed": "Hình xoá",
        "geo_added": "Hình thêm",
        "geo_changed": "Hình sửa",
        "removed": "Text xoá",
        "added": "Text thêm",
        "changed": "Text sửa",
    }

    @property
    def label(self) -> str:
        return self.LABELS.get(self.kind, self.kind)


def compare_page(d1: PdfDoc, d2: PdfDoc, page: int, dpi: float = 100, ink_thr: int = 200,
                 tol: int = 1, offset=(0.0, 0.0), do_pixel=True, do_text=True,
                 pdiff: PixelDiff | None = None) -> tuple[list[DiffItem], PixelDiff | None]:
    items: list[DiffItem] = []
    if not (d1.has_page(page) and d2.has_page(page)):
        return items, None
    z = dpi / 72.0
    if do_pixel:
        if pdiff is None:
            g1 = d1.render(page, dpi, gray=True)
            g2 = d2.render(page, dpi, gray=True)
            pdiff = pixel_diff(g1, g2, dpi, ink_thr, tol,
                               (int(round(-offset[0] * z)), int(round(-offset[1] * z))))
        z = pdiff.dpi / 72.0
        cell = max(6, int(round(8 * z)))
        for (x0, y0, x1, y1, _cnt) in mask_regions(pdiff.removed | pdiff.added, cell=cell,
                                                   min_px=max(3, int(z * 2))):
            nr = int(pdiff.removed[y0:y1, x0:x1].sum())
            na = int(pdiff.added[y0:y1, x0:x1].sum())
            if nr and na:
                kind = "geo_changed"
            elif nr:
                kind = "geo_removed"
            else:
                kind = "geo_added"
            items.append(DiffItem(kind, page, (x0 / z, y0 / z, x1 / z, y1 / z),
                                  f"-{nr} / +{na} px"))
    if do_text:
        td = text_diff(d1.words(page), d2.words(page), offset)
        dx, dy = offset
        for w in td.removed:
            items.append(DiffItem("removed", page, tuple(w[:4]), w[4]))
        for w in td.added:
            items.append(DiffItem("added", page, (w[0] - dx, w[1] - dy, w[2] - dx, w[3] - dy), w[4]))
        for a, b in td.changed:
            r = fitz.Rect(a[:4]) | fitz.Rect(b[0] - dx, b[1] - dy, b[2] - dx, b[3] - dy)
            items.append(DiffItem("changed", page, tuple(r), f"{a[4]}  →  {b[4]}"))
    return items, pdiff


# --------------------------------------------------------------------------- #
#  Zones
# --------------------------------------------------------------------------- #
RULES = OrderedDict([
    ("compare", "So sánh Ver1 ↔ Ver2"),
    ("extract", "Chỉ trích xuất data"),
    ("contains", "Chứa text"),
    ("equals", "Bằng chính xác"),
    ("regex", "Khớp Regex"),
    ("not_empty", "Không được rỗng"),
    ("number_range", "Số trong khoảng (min-max)"),
])


@dataclass
class Zone:
    name: str
    page: int
    rect: tuple
    rule: str = "compare"
    expected: str = ""
    status: str = ""          # "" | OK | CHANGED | FAIL | N/A | DATA
    text_v1: str = ""
    text_v2: str = ""
    detail: str = ""
    geo_changed_px: int = 0

    def to_json(self) -> dict:
        return {k: v for k, v in asdict(self).items()
                if k in ("name", "page", "rect", "rule", "expected")}

    @staticmethod
    def from_json(d: dict) -> "Zone":
        return Zone(d["name"], int(d["page"]), tuple(d["rect"]), d.get("rule", "compare"),
                    d.get("expected", ""))


_NUM_RE = re.compile(r"[-+]?\d+(?:[.,]\d+)?")


def _rule_ok(rule: str, expected: str, text: str) -> tuple[bool, str]:
    t = text.strip()
    if rule == "contains":
        return (expected.lower() in t.lower()), f"cần chứa '{expected}'"
    if rule == "equals":
        return (t == expected.strip()), f"cần bằng '{expected}'"
    if rule == "regex":
        try:
            return (re.search(expected, t) is not None), f"regex /{expected}/"
        except re.error as e:
            return False, f"regex lỗi: {e}"
    if rule == "not_empty":
        return bool(t), "không rỗng"
    if rule == "number_range":
        try:
            lo, hi = [float(x) for x in expected.replace(" ", "").split("-", 1)]
        except Exception:
            return False, "expected phải dạng min-max, vd 100-500"
        nums = [float(n.replace(",", ".")) for n in _NUM_RE.findall(t)]
        if not nums:
            return False, "không tìm thấy số"
        bad = [n for n in nums if not (lo <= n <= hi)]
        return (not bad), (f"ngoài khoảng: {bad}" if bad else f"{lo}-{hi}")
    return True, ""


def check_zone(z: Zone, d1: PdfDoc | None, d2: PdfDoc | None, offset=(0.0, 0.0),
               dpi: float = 200, ink_thr: int = 200, tol: int = 1, geo_thr: int = 20) -> Zone:
    r1 = fitz.Rect(z.rect)
    r2 = fitz.Rect(r1.x0 + offset[0], r1.y0 + offset[1], r1.x1 + offset[0], r1.y1 + offset[1])
    has1 = d1 is not None and d1.has_page(z.page)
    has2 = d2 is not None and d2.has_page(z.page)
    z.text_v1 = "\n".join(d1.lines_in(z.page, r1)) if has1 else ""
    z.text_v2 = "\n".join(d2.lines_in(z.page, r2)) if has2 else ""
    z.geo_changed_px = 0
    if has1 and has2:
        g1 = d1.render(z.page, dpi, gray=True, clip=r1)
        g2 = d2.render(z.page, dpi, gray=True, clip=r2)
        pd = pixel_diff(g1, g2, dpi, ink_thr, tol)
        z.geo_changed_px = pd.n_removed + pd.n_added

    changed = has1 and has2 and (z.text_v1 != z.text_v2 or z.geo_changed_px > geo_thr)
    if z.rule == "compare":
        if not (has1 and has2):
            z.status, z.detail = "N/A", "cần mở cả Ver1 và Ver2"
        elif changed:
            z.status = "CHANGED"
            z.detail = ("text khác; " if z.text_v1 != z.text_v2 else "") + f"{z.geo_changed_px} px hình khác"
        else:
            z.status, z.detail = "OK", "giống nhau"
        return z

    target = z.text_v2 if has2 else z.text_v1
    if z.rule == "extract":
        n = len([l for l in target.splitlines() if l.strip()])
        z.status = "DATA"
        z.detail = f"{n} dòng" + ("; Ver1↔Ver2 có thay đổi" if changed else "")
        return z
    ok, why = _rule_ok(z.rule, z.expected, target)
    z.status = "OK" if ok else "FAIL"
    z.detail = why + ("; Ver1↔Ver2 có thay đổi" if changed else "")
    return z


# --------------------------------------------------------------------------- #
#  Query
# --------------------------------------------------------------------------- #
@dataclass
class QueryHit:
    ver: int
    page: int
    rect: tuple
    text: str
    changed: bool = False


def query_words(doc: PdfDoc, ver: int, pages: list[int], pattern: str, regex=False,
                case=False, rects: dict[int, list] | None = None) -> list[QueryHit]:
    flags = 0 if case else re.IGNORECASE
    try:
        rx = re.compile(pattern if regex else re.escape(pattern), flags)
    except re.error:
        return []
    hits = []
    for p in pages:
        if not doc.has_page(p):
            continue
        for w in doc.words(p):
            if not rx.search(w[4]):
                continue
            if rects is not None:
                cx, cy = (w[0] + w[2]) / 2, (w[1] + w[3]) / 2
                if not any(r[0] <= cx <= r[2] and r[1] <= cy <= r[3] for r in rects.get(p, [])):
                    continue
            hits.append(QueryHit(ver, p, tuple(w[:4]), w[4]))
    return hits


# --------------------------------------------------------------------------- #
#  Highlight markups (Bluebeam-like)
# --------------------------------------------------------------------------- #
@dataclass
class Highlight:
    id: int
    ver: int                  # 1 / 2
    page: int
    rects: list               # display coords, one rect per text line (or one for area)
    color: str = "#ffe600"    # hex
    opacity: float = 0.45
    kind: str = "text"        # text | area
    text: str = ""
    comment: str = ""
    author: str = ""
    date: str = ""

    def bbox(self) -> tuple:
        r = fitz.Rect(self.rects[0])
        for x in self.rects[1:]:
            r |= fitz.Rect(x)
        return tuple(r)

    def to_json(self) -> dict:
        return asdict(self)

    @staticmethod
    def from_json(d: dict) -> "Highlight":
        d = dict(d)
        d["rects"] = [tuple(r) for r in d["rects"]]
        return Highlight(**d)


def snap_text_rects(doc: PdfDoc, page: int, rect: fitz.Rect, min_overlap: float = 0.3) -> tuple[list, str]:
    """Words overlapping rect -> one rect per text line (+ joined text). Empty if no text."""
    hits = []
    for w in doc.words(page):
        wr = fitz.Rect(w[:4])
        inter = wr & rect
        if inter.is_empty or wr.is_empty:
            continue
        if (inter.width * inter.height) / max(wr.width * wr.height, 1e-6) >= min_overlap:
            hits.append(w)
    if not hits:
        return [], ""
    groups: dict[tuple, list] = defaultdict(list)
    for w in hits:
        groups[(w[5], w[6])].append(w)
    rects = []
    for ws in groups.values():
        r = fitz.Rect(ws[0][:4])
        for w in ws[1:]:
            r |= fitz.Rect(w[:4])
        rects.append(tuple(r))
    rects.sort(key=lambda r: (round(r[1] / 3), r[0]))
    return rects, "\n".join(words_to_lines(hits))


def _hex_rgb(h: str) -> tuple:
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))


def export_highlights(src_path: str, out_path: str, items: list[Highlight]) -> int:
    """Write highlights as real PDF annotations into a copy of src_path."""
    d = fitz.open(src_path)
    n = 0
    for h in items:
        if not (0 <= h.page < d.page_count):
            continue
        pg = d[h.page]
        der = pg.derotation_matrix if pg.rotation else None
        rs = []
        for r in h.rects:
            r = fitz.Rect(r)
            if der is not None:
                r = r * der
                r.normalize()
            rs.append(r)
        col = _hex_rgb(h.color)
        if h.kind == "text":
            a = pg.add_highlight_annot(quads=rs)
            a.set_colors(stroke=col)
        else:
            a = pg.add_rect_annot(rs[0])
            a.set_colors(stroke=col, fill=col)
            a.set_border(width=0)
            a.set_blendmode(fitz.PDF_BM_Multiply)
        a.set_opacity(h.opacity)
        a.set_info(content=h.comment or h.text, title=h.author or "PDF QA", subject="Highlight")
        a.update()
        n += 1
    d.save(out_path, garbage=3, deflate=True)
    d.close()
    return n


# --------------------------------------------------------------------------- #
#  Crop export
# --------------------------------------------------------------------------- #
def export_crop_pdf(out_path: str, items: list[tuple[PdfDoc, int, fitz.Rect]]):
    """Vector crop. items: (doc, page, display-rect)."""
    nd = fitz.open()
    for doc, pno, rect in items:
        src_pg = doc.page(pno)
        clip = doc.to_unrotated(pno, rect) & src_pg.cropbox
        if clip.is_empty:
            continue
        pg = nd.new_page(width=clip.width, height=clip.height)
        pg.show_pdf_page(pg.rect, doc.doc, pno, clip=clip)
        if src_pg.rotation:
            pg.set_rotation(src_pg.rotation)
    if nd.page_count == 0:
        raise ValueError("Vùng crop rỗng")
    nd.save(out_path, garbage=3, deflate=True)
    nd.close()


def save_rgb_png(arr: np.ndarray, path: str):
    h, w = arr.shape[:2]
    if arr.ndim == 2:
        pix = fitz.Pixmap(fitz.csGRAY, w, h, np.ascontiguousarray(arr).tobytes(), False)
    else:
        pix = fitz.Pixmap(fitz.csRGB, w, h, np.ascontiguousarray(arr).tobytes(), False)
    pix.save(path)
