/**
 * Client-side API Facade.
 * Intercepts all backend calls and routes them directly to client_core.js (runs 100% in browser).
 */
import { clientCore } from './client_core.js';
import { downloadBlob } from './ui.js';

export async function getJSON(url) {
  if (url.includes('/api/meta')) {
    return {
      rules: {
        compare: "So sánh Ver1 ↔ Ver2",
        extract: "Chỉ trích xuất data",
        contains: "Chứa text",
        equals: "Bằng chính xác",
        regex: "Khớp Regex",
        not_empty: "Không được rỗng",
        number_range: "Số trong khoảng (min-max)"
      },
      labels: {
        geo_removed: "Hình xoá",
        geo_added: "Hình thêm",
        geo_changed: "Hình sửa",
        removed: "Text xoá",
        added: "Text thêm",
        changed: "Text sửa"
      },
      user: "User"
    };
  }
  if (url.startsWith('/api/docs/')) {
    const id = url.split('/api/docs/')[1].split('/')[0];
    const doc = clientCore.getDoc(id);
    if (!doc) throw new Error('Không tìm thấy tài liệu ' + id);
    return { id: doc.id, name: doc.name, page_count: doc.page_count, pages: doc.pages };
  }
  return {};
}

export async function postJSON(url, body) {
  if (url.includes('/api/compare')) {
    return clientCore.comparePages(body);
  }
  if (url.includes('/api/read')) {
    return clientCore.readRegion(body);
  }
  if (url.includes('/api/zones/check')) {
    return clientCore.checkZones(body);
  }
  if (url.includes('/api/query')) {
    return clientCore.queryText(body);
  }
  if (url.includes('/api/snap')) {
    return clientCore.snapHighlight(body);
  }
  if (url.includes('/api/text_in')) {
    const rr = await clientCore.readRegion({ d1: body.doc, page: body.page, rect: body.rect });
    return { lines: rr.rows.map(x => x.text1 || x.text2).filter(Boolean) };
  }
  return {};
}

export async function del(url) {
  if (url.startsWith('/api/docs/')) {
    const id = url.split('/api/docs/')[1].split('/')[0];
    clientCore.closeDocument(id);
  }
}

export async function postDownload(url, body, fallbackName = 'download') {
  if (url.includes('/api/crop')) {
    await clientCore.cropExport(body);
    return 'crop.png';
  }
  if (url.includes('/api/highlights/export')) {
    // Download markup annotations JSON
    const blob = new Blob([JSON.stringify(body.items || [], null, 2)], { type: 'application/json' });
    downloadBlob(blob, fallbackName || 'markup.json');
    return fallbackName;
  }
  return fallbackName;
}

export async function upload(url, fields, onProgress) {
  if (url.includes('/api/batch/file')) {
    const doc = await clientCore.loadDocument(fields.file, onProgress);
    const temps = fields.templates ? JSON.parse(fields.templates) : [];
    const checkRes = await clientCore.checkZones({ d1: doc.id, zones: temps });
    return { name: doc.name, doc: doc.id, results: checkRes.results };
  }
  if (fields.file) {
    if (window.pdfjsLib) {
      try {
        return await clientCore.loadDocument(fields.file, onProgress);
      } catch (e) {
        console.warn('clientCore.loadDocument failed, falling back to server upload...', e);
      }
    }
    // Backend fallback
    return new Promise((resolve, reject) => {
      const fd = new FormData();
      for (const [k, v] of Object.entries(fields)) fd.append(k, v);
      const xhr = new XMLHttpRequest();
      xhr.open('POST', url);
      xhr.responseType = 'json';
      xhr.upload.onprogress = e => e.lengthComputable && onProgress?.(e.loaded / e.total);
      xhr.onload = () => {
        if (xhr.status >= 200 && xhr.status < 300) resolve(xhr.response);
        else reject(new Error(xhr.response?.detail || `${xhr.status} ${xhr.statusText}`));
      };
      xhr.onerror = () => reject(new Error('Mất kết nối tới server'));
      xhr.send(fd);
    });
  }
  throw new Error('No file provided');
}

export function excel(filename, sheets) {
  return clientCore.exportExcel(filename, sheets);
}

export function renderPage(docId, pageIdx, dpi, clip) {
  if (clientCore.getDoc(docId)) {
    return clientCore.renderPage(docId, pageIdx, dpi, clip);
  }
  let u = `/api/docs/${encodeURIComponent(docId)}/render?page=${pageIdx}&dpi=${dpi}`;
  if (clip) u += `&clip=${clip.join(',')}`;
  return u;
}

export function renderOverlay(opts) {
  if (clientCore.getDoc(opts.d1)) {
    return clientCore.renderOverlay(opts);
  }
  const q = new URLSearchParams({
    d1: opts.d1, d2: opts.d2, page: opts.page,
    thr: opts.thr, tol: opts.tol, dx: opts.dx, dy: opts.dy, dpi: opts.dpi
  });
  if (opts.clip) q.set('clip', opts.clip.join(','));
  return `/api/overlay?${q.toString()}`;
}
