/**
 * Client-side API Facade.
 * Intercepts all backend calls and routes them directly to client_core.js (runs 100% in browser).
 */
import { clientCore } from './client_core.js';
import { downloadBlob } from './ui.js';

export async function getJSON(url) {
  if (url.includes('/api/meta')) {
    return { rules: {}, labels: {}, user: 'Web' };
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
    return clientCore.loadDocument(fields.file, onProgress);
  }
  throw new Error('No file provided');
}

export function excel(filename, sheets) {
  return clientCore.exportExcel(filename, sheets);
}

export function renderPage(docId, pageIdx, dpi, clip) {
  return clientCore.renderPage(docId, pageIdx, dpi, clip);
}

export function renderOverlay(opts) {
  return clientCore.renderOverlay(opts);
}
