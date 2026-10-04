/**
 * Pure client-side PDF QA Engine for Web & GitHub Pages.
 * Replaces Python backend using Mozilla PDF.js, HTML5 Canvas, SheetJS, and PDF-Lib.
 */

// Colors matching Python core.py
export const COL_REMOVED = [235, 64, 52];     // Red
export const COL_ADDED   = [32, 170, 90];     // Green
export const COL_COMMON  = [150, 156, 168];   // Grey
export const COL_BG      = [255, 255, 255];   // White

class ClientDoc {
  constructor(id, name, pdfDoc, arrayBuffer, pages) {
    this.id = id;
    this.name = name;
    this.pdfDoc = pdfDoc;
    this.arrayBuffer = arrayBuffer;
    this.page_count = pages.length;
    this.pages = pages; // [[w, h], ...] in PDF points
    this._wordsCache = {}; // pageIdx -> [{x0, y0, x1, y1, text, ...}]
  }

  async getWords(pageIdx) {
    if (this._wordsCache[pageIdx]) return this._wordsCache[pageIdx];
    const page = await this.pdfDoc.getPage(pageIdx + 1);
    const tc = await page.getTextContent();
    const vp = page.getViewport({ scale: 1.0 });
    const words = [];

    // Parse text items into display coordinates (rotation and transform aware)
    for (const item of tc.items) {
      if (!item.str || !item.str.trim()) continue;
      const [a, b, c, d, e, f] = item.transform;
      const h = Math.hypot(b, d) || item.height || 12;
      const w = item.width || (item.str.length * h * 0.6);

      // Four corners in PDF coordinates
      const vp0 = vp.convertToViewportPoint(e, f);
      const vp1 = vp.convertToViewportPoint(a * w + e, b * w + f);
      const vp2 = vp.convertToViewportPoint(c * h + e, d * h + f);
      const vp3 = vp.convertToViewportPoint(a * w + c * h + e, b * w + d * h + f);

      const x0 = Math.min(vp0[0], vp1[0], vp2[0], vp3[0]);
      const x1 = Math.max(vp0[0], vp1[0], vp2[0], vp3[0]);
      const y0 = Math.min(vp0[1], vp1[1], vp2[1], vp3[1]);
      const y1 = Math.max(vp0[1], vp1[1], vp2[1], vp3[1]);

      // Split into space-separated words
      const parts = item.str.split(/\s+/);
      if (parts.length <= 1) {
        words.push({ x0, y0, x1, y1, text: item.str });
      } else {
        const step = (x1 - x0) / item.str.length;
        let curX = x0;
        for (const p of parts) {
          if (!p) continue;
          const pw = p.length * step;
          words.push({ x0: curX, y0, x1: curX + pw, y1, text: p });
          curX += pw + step;
        }
      }
    }
    this._wordsCache[pageIdx] = words;
    return words;
  }
}

class ClientCore {
  constructor() {
    this.docs = new Map();
  }

  async loadDocument(file, onProgress) {
    const ab = await file.arrayBuffer();
    const pdfjs = window.pdfjsLib;
    if (!pdfjs) throw new Error('Thư viện PDF.js chưa sẵn sàng.');

    const loadingTask = pdfjs.getDocument({
      data: new Uint8Array(ab),
      cMapUrl: 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/cmaps/',
      cMapPacked: true,
    });

    if (onProgress) {
      loadingTask.onProgress = p => {
        if (p.total > 0) onProgress(p.loaded / p.total);
      };
    }

    const pdfDoc = await loadingTask.promise;
    const pages = [];
    for (let i = 1; i <= pdfDoc.numPages; i++) {
      const page = await pdfDoc.getPage(i);
      const vp = page.getViewport({ scale: 1.0 });
      pages.push([vp.width, vp.height]);
    }

    const id = 'doc_' + Math.random().toString(36).slice(2, 10);
    const doc = new ClientDoc(id, file.name, pdfDoc, ab, pages);
    this.docs.set(id, doc);

    return {
      id: doc.id,
      name: doc.name,
      page_count: doc.page_count,
      pages: doc.pages,
    };
  }

  getDoc(id) {
    return this.docs.get(id);
  }

  closeDocument(id) {
    this.docs.delete(id);
  }

  // ------------------------------------------------------------- Rendering
  async renderPage(docId, pageIdx, dpi = 100, clip = null) {
    const doc = this.getDoc(docId);
    if (!doc) throw new Error('Không tìm thấy tài liệu ' + docId);
    const page = await doc.pdfDoc.getPage(pageIdx + 1);
    const scale = dpi / 72.0;

    if (clip) {
      const [x0, y0, x1, y1] = clip;
      const w = Math.max(1, Math.round((x1 - x0) * scale));
      const h = Math.max(1, Math.round((y1 - y0) * scale));
      const canvas = document.createElement('canvas');
      canvas.width = w; canvas.height = h;
      const ctx = canvas.getContext('2d');
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, w, h);

      const vp = page.getViewport({
        scale,
        offsetX: -x0 * scale,
        offsetY: -y0 * scale,
      });
      await page.render({ canvasContext: ctx, viewport: vp }).promise;
      return canvasToBlobUrl(canvas);
    } else {
      const vp = page.getViewport({ scale });
      const canvas = document.createElement('canvas');
      canvas.width = Math.round(vp.width);
      canvas.height = Math.round(vp.height);
      const ctx = canvas.getContext('2d');
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, canvas.width, canvas.height);

      await page.render({ canvasContext: ctx, viewport: vp }).promise;
      return canvasToBlobUrl(canvas);
    }
  }

  async renderOverlay({ d1, d2, page, thr = 200, tol = 1, dx = 0, dy = 0, dpi = 100, clip = null }) {
    const doc1 = this.getDoc(d1);
    const doc2 = this.getDoc(d2);
    if (!doc1 || !doc2) throw new Error('Không tìm thấy 2 tài liệu so sánh.');

    const scale = dpi / 72.0;
    const p = page;

    // Render page 1
    const pg1 = await doc1.pdfDoc.getPage(p + 1);
    const vp1 = pg1.getViewport({ scale });
    const c1 = document.createElement('canvas');
    c1.width = Math.round(vp1.width); c1.height = Math.round(vp1.height);
    const ctx1 = c1.getContext('2d');
    ctx1.fillStyle = '#fff'; ctx1.fillRect(0, 0, c1.width, c1.height);
    await pg1.render({ canvasContext: ctx1, viewport: vp1 }).promise;

    // Render page 2
    const pg2 = await doc2.pdfDoc.getPage(p + 1);
    const vp2 = pg2.getViewport({ scale });
    const c2 = document.createElement('canvas');
    c2.width = Math.round(vp2.width); c2.height = Math.round(vp2.height);
    const ctx2 = c2.getContext('2d');
    ctx2.fillStyle = '#fff'; ctx2.fillRect(0, 0, c2.width, c2.height);
    await pg2.render({ canvasContext: ctx2, viewport: vp2 }).promise;

    const w = Math.max(c1.width, c2.width);
    const h = Math.max(c1.height, c2.height);

    const outCanvas = document.createElement('canvas');
    outCanvas.width = w; outCanvas.height = h;
    const outCtx = outCanvas.getContext('2d');
    const outImg = outCtx.createImageData(w, h);
    const outData = outImg.data;
    outData.fill(255);

    const d1Data = ctx1.getImageData(0, 0, c1.width, c1.height).data;
    const d2Data = ctx2.getImageData(0, 0, c2.width, c2.height).data;
    const w1 = c1.width, h1 = c1.height;
    const w2 = c2.width, h2 = c2.height;

    const shiftX = Math.round(dx * scale);
    const shiftY = Math.round(dy * scale);

    // Fast ink test loop
    for (let y = 0; y < h; y++) {
      const rowOffset = y * w;
      for (let x = 0; x < w; x++) {
        let ink1 = false, ink2 = false;

        if (x < w1 && y < h1) {
          const i1 = (y * w1 + x) << 2;
          if (d1Data[i1 + 3] > 30) {
            const lum1 = (d1Data[i1] * 299 + d1Data[i1 + 1] * 587 + d1Data[i1 + 2] * 114) / 1000;
            ink1 = lum1 < thr;
          }
        }

        const x2 = x - shiftX, y2 = y - shiftY;
        if (x2 >= 0 && x2 < w2 && y2 >= 0 && y2 < h2) {
          const i2 = (y2 * w2 + x2) << 2;
          if (d2Data[i2 + 3] > 30) {
            const lum2 = (d2Data[i2] * 299 + d2Data[i2 + 1] * 587 + d2Data[i2 + 2] * 114) / 1000;
            ink2 = lum2 < thr;
          }
        }

        const outI = (rowOffset + x) << 2;
        if (ink1 && !ink2) {
          // Red
          outData[outI] = COL_REMOVED[0]; outData[outI + 1] = COL_REMOVED[1]; outData[outI + 2] = COL_REMOVED[2];
        } else if (ink2 && !ink1) {
          // Green
          outData[outI] = COL_ADDED[0]; outData[outI + 1] = COL_ADDED[1]; outData[outI + 2] = COL_ADDED[2];
        } else if (ink1 && ink2) {
          // Grey
          outData[outI] = COL_COMMON[0]; outData[outI + 1] = COL_COMMON[1]; outData[outI + 2] = COL_COMMON[2];
        }
      }
    }

    outCtx.putImageData(outImg, 0, 0);

    if (clip) {
      const [cx0, cy0, cx1, cy1] = clip;
      const cw = Math.max(1, Math.round((cx1 - cx0) * scale));
      const ch = Math.max(1, Math.round((cy1 - cy0) * scale));
      const clipCanvas = document.createElement('canvas');
      clipCanvas.width = cw; clipCanvas.height = ch;
      const clipCtx = clipCanvas.getContext('2d');
      clipCtx.drawImage(outCanvas, cx0 * scale, cy0 * scale, cw, ch, 0, 0, cw, ch);
      return canvasToBlobUrl(clipCanvas);
    }

    return canvasToBlobUrl(outCanvas);
  }

  // ------------------------------------------------------------- Comparison
  async comparePages({ d1, d2, page, thr = 200, tol = 1, dx = 0, dy = 0 }) {
    const doc1 = this.getDoc(d1), doc2 = this.getDoc(d2);
    if (!doc1 || !doc2) return { items: [] };

    const items = [];
    const p = page;
    const words1 = await doc1.getWords(p);
    const words2 = await doc2.getWords(p);

    // 1. Text difference analysis
    // Map words by normalized coordinate grid (bucket ~10pt)
    const key = (x, y) => `${Math.round(x / 10)},${Math.round(y / 10)}`;
    const map2 = new Map();
    for (const w of words2) {
      // align w to Ver1 coords
      const ax = w.x0 - dx, ay = w.y0 - dy;
      const k = key(ax, ay);
      if (!map2.has(k)) map2.set(k, []);
      map2.get(k).push(w);
    }

    const matched2 = new Set();

    for (const w1 of words1) {
      const k = key(w1.x0, w1.y0);
      const candidates = map2.get(k) || [];
      let foundExact = null;
      let foundDifferent = null;

      for (const w2 of candidates) {
        if (matched2.has(w2)) continue;
        if (w1.text === w2.text) {
          foundExact = w2;
          break;
        } else {
          foundDifferent = w2;
        }
      }

      if (foundExact) {
        matched2.add(foundExact);
      } else if (foundDifferent) {
        matched2.add(foundDifferent);
        items.push({
          kind: 'changed',
          text: `${w1.text} → ${foundDifferent.text}`,
          rect: [w1.x0, w1.y0, Math.max(w1.x1, foundDifferent.x1 - dx), Math.max(w1.y1, foundDifferent.y1 - dy)],
          page: p,
        });
      } else {
        items.push({
          kind: 'removed',
          text: w1.text,
          rect: [w1.x0, w1.y0, w1.x1, w1.y1],
          page: p,
        });
      }
    }

    // Remaining unmatched in Ver2 are added
    for (const w2 of words2) {
      if (!matched2.has(w2)) {
        items.push({
          kind: 'added',
          text: w2.text,
          rect: [w2.x0 - dx, w2.y0 - dy, w2.x1 - dx, w2.y1 - dy],
          page: p,
        });
      }
    }

    // 2. Pixel diff clustering (Fast grid sample at 72 DPI)
    const scale = 1.0;
    const pg1 = await doc1.pdfDoc.getPage(p + 1);
    const pg2 = await doc2.pdfDoc.getPage(p + 1);
    const vp1 = pg1.getViewport({ scale });
    const vp2 = pg2.getViewport({ scale });

    const c1 = document.createElement('canvas');
    c1.width = Math.round(vp1.width); c1.height = Math.round(vp1.height);
    await pg1.render({ canvasContext: c1.getContext('2d'), viewport: vp1 }).promise;

    const c2 = document.createElement('canvas');
    c2.width = Math.round(vp2.width); c2.height = Math.round(vp2.height);
    await pg2.render({ canvasContext: c2.getContext('2d'), viewport: vp2 }).promise;

    const w = Math.max(c1.width, c2.width);
    const h = Math.max(c1.height, c2.height);
    const d1Data = c1.getContext('2d').getImageData(0, 0, c1.width, c1.height).data;
    const d2Data = c2.getContext('2d').getImageData(0, 0, c2.width, c2.height).data;

    const cell = 20; // 20pt grid cells
    const gw = Math.ceil(w / cell);
    const gh = Math.ceil(h / cell);
    const gridRem = new Int32Array(gw * gh);
    const gridAdd = new Int32Array(gw * gh);

    const shiftX = Math.round(dx);
    const shiftY = Math.round(dy);

    for (let y = 0; y < h; y += 2) { // step 2 for speed
      const gy = Math.floor(y / cell);
      for (let x = 0; x < w; x += 2) {
        let ink1 = false, ink2 = false;
        if (x < c1.width && y < c1.height) {
          const i1 = (y * c1.width + x) << 2;
          ink1 = (d1Data[i1] * 299 + d1Data[i1 + 1] * 587 + d1Data[i1 + 2] * 114) / 1000 < thr;
        }
        const x2 = x - shiftX, y2 = y - shiftY;
        if (x2 >= 0 && x2 < c2.width && y2 >= 0 && y2 < c2.height) {
          const i2 = (y2 * c2.width + x2) << 2;
          ink2 = (d2Data[i2] * 299 + d2Data[i2 + 1] * 587 + d2Data[i2 + 2] * 114) / 1000 < thr;
        }

        const gx = Math.floor(x / cell);
        const gi = gy * gw + gx;
        if (ink1 && !ink2) gridRem[gi]++;
        else if (ink2 && !ink1) gridAdd[gi]++;
      }
    }

    // Cluster diff cells (threshold >= 6 diff sample pixels)
    for (const [grid, kind] of [[gridRem, 'geo_removed'], [gridAdd, 'geo_added']]) {
      for (let gy = 0; gy < gh; gy++) {
        for (let gx = 0; gx < gw; gx++) {
          if (grid[gy * gw + gx] >= 6) {
            // Find bounds
            let rx0 = gx * cell, ry0 = gy * cell, rx1 = (gx + 1) * cell, ry1 = (gy + 1) * cell;
            // check if covered by text diff item already
            const isTextDiff = items.some(it =>
              it.rect[0] <= rx1 && it.rect[2] >= rx0 && it.rect[1] <= ry1 && it.rect[3] >= ry0
            );
            if (!isTextDiff) {
              items.push({
                kind,
                text: kind === 'geo_removed' ? 'Nét vẽ xoá (Ver1)' : 'Nét vẽ mới (Ver2)',
                rect: [rx0, ry0, rx1, ry1],
                page: p,
              });
            }
          }
        }
      }
    }

    return { items };
  }

  // ------------------------------------------------------------- Read region
  async readRegion({ d1, d2, page, rect, dx = 0, dy = 0 }) {
    const doc1 = this.getDoc(d1), doc2 = this.getDoc(d2);
    const p = page;
    const rx0 = Math.min(rect[0], rect[2]);
    const ry0 = Math.min(rect[1], rect[3]);
    const rx1 = Math.max(rect[0], rect[2]);
    const ry1 = Math.max(rect[1], rect[3]);

    const extractLines = async (doc, offX = 0, offY = 0) => {
      if (!doc) return [];
      const words = await doc.getWords(p);
      const inBox = words.filter(w => {
        const wx0 = w.x0 + offX, wx1 = w.x1 + offX;
        const wy0 = w.y0 + offY, wy1 = w.y1 + offY;
        const cx = (wx0 + wx1) / 2;
        const cy = (wy0 + wy1) / 2;
        return (cx >= rx0 - 2 && cx <= rx1 + 2 && cy >= ry0 - 2 && cy <= ry1 + 2) ||
               !(wx1 < rx0 || wx0 > rx1 || wy1 < ry0 || wy0 > ry1);
      });
      // Sort into lines by Y then X
      inBox.sort((a, b) => Math.abs(a.y0 - b.y0) > 5 ? a.y0 - b.y0 : a.x0 - b.x0);
      const lines = [];
      let curLine = [];
      let curY = -999;
      for (const w of inBox) {
        if (Math.abs(w.y0 - curY) > 6) {
          if (curLine.length) lines.push(curLine.map(item => item.text).join(' '));
          curLine = [w];
          curY = w.y0;
        } else {
          curLine.push(w);
        }
      }
      if (curLine.length) lines.push(curLine.map(item => item.text).join(' '));
      return lines;
    };

    const lines1 = await extractLines(doc1, 0, 0);
    const lines2 = await extractLines(doc2, -dx, -dy);

    const maxLen = Math.max(lines1.length, lines2.length);
    const rows = [];
    for (let i = 0; i < maxLen; i++) {
      const t1 = lines1[i] || '';
      const t2 = lines2[i] || '';
      let op = 'equal';
      if (!t1 && t2) op = 'added';
      else if (t1 && !t2) op = 'removed';
      else if (t1 !== t2) op = 'changed';
      rows.push({ op, text1: t1, text2: t2 });
    }

    return { rows };
  }

  // ------------------------------------------------------------- Check Zones
  async checkZones({ d1, d2, zones, dx = 0, dy = 0 }) {
    const results = [];
    for (const z of zones) {
      const p = z.page || 0;
      const rr = await this.readRegion({ d1, d2, page: p, rect: z.rect, dx, dy });
      const text1 = rr.rows.map(r => r.text1).filter(Boolean).join(' ').trim();
      const text2 = rr.rows.map(r => r.text2).filter(Boolean).join(' ').trim();
      const val = text2 || text1;

      let status = 'OK';
      let detail = '';
      const rule = z.rule || z.rule_type || 'compare';
      const expected = (z.expected || z.rule_val || '').trim();

      if (rule === 'compare') {
        if (!d1 || !d2) {
          status = 'DATA';
          detail = val ? 'Đã đọc text' : 'Vùng trống';
        } else if (text1 === text2) {
          status = 'OK';
          detail = 'Trùng khớp';
        } else {
          status = 'CHANGED';
          detail = `${text1 || '—'} ≠ ${text2 || '—'}`;
        }
      } else if (rule === 'extract') {
        status = 'DATA';
        detail = val ? 'Đã đọc text' : 'Vùng trống';
      } else if (rule === 'equals') {
        status = val.toLowerCase() === expected.toLowerCase() ? 'OK' : 'FAIL';
        detail = status === 'OK' ? `Bằng: "${val}"` : `Khác: "${val}" ≠ "${expected}"`;
      } else if (rule === 'contains') {
        status = val.toLowerCase().includes(expected.toLowerCase()) ? 'OK' : 'FAIL';
        detail = status === 'OK' ? `Chứa: "${expected}"` : `Không chứa: "${expected}"`;
      } else if (rule === 'regex') {
        try {
          const re = new RegExp(expected, 'i');
          status = re.test(val) ? 'OK' : 'FAIL';
          detail = status === 'OK' ? `Khớp /${expected}/` : `Không khớp /${expected}/`;
        } catch {
          status = 'FAIL';
          detail = 'Regex không hợp lệ';
        }
      } else if (rule === 'not_empty') {
        status = val.length > 0 ? 'OK' : 'FAIL';
        detail = status === 'OK' ? `Có text (${val.length} ký tự)` : 'Vùng để trống';
      } else if (rule === 'number_range') {
        const nums = (val.match(/[-+]?\d*\.?\d+/g) || []).map(Number);
        const parts = expected.split(/[-–—,]/).map(s => parseFloat(s.trim())).filter(n => !isNaN(n));
        if (parts.length >= 2 && nums.length > 0) {
          const [min, max] = [Math.min(parts[0], parts[1]), Math.max(parts[0], parts[1])];
          const ok = nums.every(n => n >= min && n <= max);
          status = ok ? 'OK' : 'FAIL';
          detail = `${nums.join(', ')} trong [${min}, ${max}]`;
        } else {
          status = nums.length ? 'OK' : 'FAIL';
          detail = nums.length ? `Số: ${nums.join(', ')}` : 'Không tìm thấy số';
        }
      }

      results.push({
        ...z,
        rule,
        expected,
        status,
        detail,
        text_v1: text1,
        text_v2: text2,
      });
    }
    return { zones: results };
  }

  // ------------------------------------------------------------- Query
  async queryText({ d1, d2, query, isRegex = false, docVer = 0 }) {
    const hits = [];
    const targetDocs = [];
    if (docVer === 1 || docVer === 0) if (d1) targetDocs.push([1, this.getDoc(d1)]);
    if (docVer === 2 || docVer === 0) if (d2) targetDocs.push([2, this.getDoc(d2)]);

    let reg;
    try {
      reg = isRegex ? new RegExp(query, 'i') : null;
    } catch {
      return { hits: [] };
    }

    const testMatch = str => reg ? reg.test(str) : str.toLowerCase().includes(query.toLowerCase());

    for (const [ver, doc] of targetDocs) {
      if (!doc) continue;
      for (let p = 0; p < doc.page_count; p++) {
        const words = await doc.getWords(p);
        for (const w of words) {
          if (testMatch(w.text)) {
            hits.push({
              ver,
              doc: doc.id,
              page: p,
              text: w.text,
              rect: [w.x0, w.y0, w.x1, w.y1],
            });
          }
        }
      }
    }
    return { hits };
  }

  // ------------------------------------------------------------- Snap
  async snapHighlight({ doc: docId, page: p, rect }) {
    const doc = this.getDoc(docId);
    if (!doc) return { rect };
    const words = await doc.getWords(p);
    const [x0, y0, x1, y1] = rect;

    // Find intersecting words
    const hit = words.filter(w =>
      !(w.x1 < x0 || w.x0 > x1 || w.y1 < y0 || w.y0 > y1)
    );

    if (!hit.length) return { rect };

    // Snap to outer bounds of matched words
    return {
      rect: [
        Math.min(...hit.map(w => w.x0)),
        Math.min(...hit.map(w => w.y0)),
        Math.max(...hit.map(w => w.x1)),
        Math.max(...hit.map(w => w.y1)),
      ],
    };
  }

  // ------------------------------------------------------------- Export Excel
  exportExcel(filename, sheets) {
    if (!window.XLSX) {
      alert('Thư viện Excel chưa được nạp.');
      return;
    }
    const wb = window.XLSX.utils.book_new();
    for (const s of sheets) {
      const data = [s.headers, ...(s.rows || [])];
      const ws = window.XLSX.utils.aoa_to_sheet(data);
      window.XLSX.utils.book_append_sheet(wb, ws, s.title || 'Sheet1');
    }
    window.XLSX.writeFile(wb, filename);
  }

  // ------------------------------------------------------------- Crop Export
  async cropExport({ d1, d2, page, regions, format = 'png', dpi = 150 }) {
    // Generate crop image from canvas
    const doc = this.getDoc(d1 || d2);
    if (!doc || !regions.length) return;
    const [x0, y0, x1, y1] = regions[0];
    const url = await this.renderPage(doc.id, page, dpi, [x0, y0, x1, y1]);

    const a = document.createElement('a');
    a.href = url;
    a.download = `crop_p${page + 1}.${format}`;
    document.body.appendChild(a);
    a.click();
    a.remove();
  }
}

function canvasToBlobUrl(canvas) {
  return new Promise(resolve => {
    canvas.toBlob(blob => {
      resolve(URL.createObjectURL(blob));
    }, 'image/png');
  });
}

export const clientCore = new ClientCore();
