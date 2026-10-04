// PDF QA Web — main application (port of pdf_qa/main_window.py).
import * as ui from './ui.js';
import { $, $$, esc, toast } from './ui.js';
import * as api from './api.js';
import { Viewer } from './viewer.js';
import installZones from './zones.js';
import installQuery from './query.js';
import installMarkup from './markup.js';

// --------------------------------------------------------------- constants
export const COL = {
  red: '#eb4034', green: '#20aa5a', orange: '#ff9f1c', blue: '#4f8cff',
  cyan: '#22d3ee', yellow: '#ffcc00', purple: '#b072ff', grey: '#7d8799',
};
const KIND_COL = {
  geo_removed: COL.red, removed: COL.red, geo_added: COL.green, added: COL.green,
  geo_changed: COL.orange, changed: COL.orange,
};
const ZONE_COL = { '': COL.purple, OK: COL.green, CHANGED: COL.orange, FAIL: COL.red, 'N/A': COL.grey, DATA: COL.blue, EMPTY: '#8892b0' };
const TOOL_COL = { pan: COL.blue, crop: COL.cyan, read: COL.yellow, zone: COL.purple, highlight: '#ffe600' };
const TOOL_HINT = {
  pan: 'Pan: kéo chuột trái để di chuyển, lăn chuột để zoom.',
  crop: 'Crop: kéo khung vùng cần cắt → xuất PDF vector / PNG.',
  read: 'Đọc data: kéo khung vùng cần đọc → so sánh text Ver1/Ver2 + tô màu khác biệt.',
  zone: 'Vùng check: kéo khung để định nghĩa vùng kiểm tra (mini zone).',
  highlight: 'Highlight: kéo qua text để tô theo dòng chữ, kéo vùng trống để tô vùng. Chuột phải vào highlight để ghi chú / đổi màu / xoá. Ctrl+Z hoàn tác.',
};
const ROW_SYM = { equal: '=', changed: '≠', removed: '−', added: '+' };

// ------------------------------------------------------------------- state
const S = {
  docs: { 1: null, 2: null },
  page: 0,
  mode: 'v1',
  tool: 'pan',
  zones: [],
  diffItems: {},
  queryHits: [],
  queryColor: '#ff4fd8',
  highlights: [],
  hlId: 0,
  hlColor: '#ffe600',
  hlSel: null,
  region: null,
  regionTd: null,
  readRows: [],
  focus: null,
  viewSrc: new Map(),
  set: { dpi: 'Auto', thr: 200, tol: 1, dx: 0, dy: 0, pixel: true, text: true, show: true },
};

const app = { S, COL, ZONE_COL, ui, api, meta: { rules: {}, labels: {}, user: '' } };
window.pdfqa = app;   // handy for debugging

const A = new Viewer($('#viewA'), { title: 'VER 1', accent: COL.blue });
const B = new Viewer($('#viewB'), { title: 'VER 2', accent: COL.green });
app.A = A; app.B = B;

// ----------------------------------------------------------------- helpers
const r2 = v => Math.round(v * 100) / 100;
app.r2 = r2;
app.rect2 = r => r.map(r2);
app.offset = () => [S.set.dx, S.set.dy];
app.hasPage = (v, p = S.page) => !!(S.docs[v] && p >= 0 && p < S.docs[v].page_count);
app.both = (p = S.page) => app.hasPage(1, p) && app.hasPage(2, p);
app.pageCount = () => Math.max(0, ...[1, 2].map(v => S.docs[v]?.page_count || 0));
app.shift = (r, dx, dy) => [r[0] + dx, r[1] + dy, r[2] + dx, r[3] + dy];
app.grow = (r, d) => [r[0] - d, r[1] - d, r[2] + d, r[3] + d];
app.toView = (r, space, src) => {
  const [dx, dy] = app.offset();
  if (src === 2 && space === 1) return app.shift(r, dx, dy);
  if (src !== 2 && space === 2) return app.shift(r, -dx, -dy);
  return r.slice();
};
app.toV1 = (r, src) => {
  const [dx, dy] = app.offset();
  return src === 2 ? app.shift(r, -dx, -dy) : r.slice();
};
app.bbox = rects => [
  Math.min(...rects.map(r => r[0])), Math.min(...rects.map(r => r[1])),
  Math.max(...rects.map(r => r[2])), Math.max(...rects.map(r => r[3])),
];
app.settings = () => ({
  d1: S.docs[1]?.id ?? null, d2: S.docs[2]?.id ?? null,
  thr: S.set.thr, tol: S.set.tol, dx: S.set.dx, dy: S.set.dy,
});
app.dpiFor = p => {
  if (S.set.dpi !== 'Auto') return +S.set.dpi;
  let long = 0;
  for (const v of [1, 2]) if (app.hasPage(v, p)) long = Math.max(long, ...S.docs[v].pages[p]);
  if (!long) return 100;
  return Math.max(50, Math.min(200, 5000 * 72 / long));
};
let msgTimer;
app.msg = (text, ms = 5000) => {
  $('#status-msg').textContent = text;
  clearTimeout(msgTimer);
  msgTimer = setTimeout(() => { $('#status-msg').textContent = ''; }, ms);
};
let busyN = 0;
app.busy = async fn => {
  busyN++; document.body.style.cursor = 'progress';
  try { return await fn(); }
  finally { if (--busyN === 0) document.body.style.cursor = ''; }
};
app.fail = e => { console.error(e); toast(esc(e.message || e), 'error', 6000); };

function darker(hex, f) {
  const n = parseInt(hex.slice(1), 16);
  const c = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map(v => Math.round(v / f));
  return '#' + c.map(v => v.toString(16).padStart(2, '0')).join('');
}
app.darker = darker;

// ------------------------------------------------------------------- tabs
app.showTab = name => {
  $$('.tab').forEach(t => t.classList.toggle('active', t.dataset.tab === name));
  $$('.panel').forEach(p => p.classList.toggle('active', p.id === `panel-${name}`));
};
$$('.tab').forEach(t => t.addEventListener('click', () => app.showTab(t.dataset.tab)));
app.setTabCount = (name, n) => {
  const t = $(`#tab-${name}`);
  let c = t.querySelector('.count');
  if (!n) { c?.remove(); return; }
  if (!c) { c = document.createElement('span'); c.className = 'count'; t.appendChild(c); }
  c.textContent = n;
};

// ============================================================== documents
async function openDoc(ver, file) {
  if (!file) {
    [file] = await ui.pickFile($(`#file${ver}`));
    if (!file) return;
  }
  if (!/\.pdf$/i.test(file.name)) { toast('Chỉ hỗ trợ file PDF', 'warn'); return; }
  const pending = S.highlights.filter(h => h.ver === ver);
  if (pending.length && S.docs[ver]) {
    const ok = await ui.confirmBox('Highlight chưa lưu',
      `PDF ${ver} đang có <b>${pending.length}</b> highlight. Mở file mới sẽ bỏ các highlight này.<br>
       Tiếp tục? (Chọn Huỷ để quay lại và <b>Lưu vào PDF</b> trước)`, 'Bỏ highlight & mở');
    if (!ok) return;
    S.highlights = S.highlights.filter(h => h.ver !== ver);
    app.refreshHlTable();
  }
  const view = (S.mode === 'side' && ver === 2) ? B : A;
  view.showProgress(`Đang tải lên ${file.name}…`, 0);
  let info;
  try {
    info = await api.upload('/api/docs', { file }, f =>
      view.showProgress(f < 1 ? `Đang tải lên ${file.name} — ${Math.round(f * 100)}%` : `Đang mở ${file.name}…`, f));
  } catch (e) {
    toast(`Không mở được file:<br>${esc(e.message)}`, 'error', 7000);
    return;
  } finally { view.hideProgress(); }
  app.setDoc(ver, info);
}
app.openDoc = openDoc;

app.setDoc = (ver, info) => {
  const old = S.docs[ver];
  if (old && !old.keep && old.id !== info.id && S.docs[3 - ver]?.id !== old.id) api.del(`/api/docs/${old.id}`);
  S.docs[ver] = info;
  S.diffItems = {};
  renderTree();
  S.region = null; S.regionTd = null;
  const btn = $(`#btn-open${ver}`);
  btn.classList.add('loaded');
  btn.title = `Ver${ver}: ${info.name} (${info.page_count} trang) — bấm để đổi file`;
  $(`#lbl-doc${ver}`).textContent = info.name;
  const n = app.pageCount();
  $('#inp-page').max = Math.max(1, n);
  $('#lbl-pages').textContent = `/ ${n}`;
  if (S.page >= n) { S.page = 0; $('#inp-page').value = 1; }
  if (S.docs[1] && S.docs[2] && (S.mode === 'v1' || S.mode === 'v2')) setMode('side');
  else if (ver === 2 && !S.docs[1]) setMode('v2');
  else refreshView();
  app.refreshZoneTable();
  renderSheetList();
  app.msg(`Đã mở Ver${ver}: ${info.name} (${info.page_count} trang)`);
  toast(`<b>Ver${ver}</b>: ${esc(info.name)} · ${info.page_count} trang`, 'ok');
};

function paperSizeName(w, h) {
  const mmW = Math.round(Math.min(w, h) * 0.3528);
  const mmH = Math.round(Math.max(w, h) * 0.3528);
  if (Math.abs(mmW - 841) < 25 && Math.abs(mmH - 1189) < 25) return 'A0';
  if (Math.abs(mmW - 594) < 20 && Math.abs(mmH - 841) < 20) return 'A1';
  if (Math.abs(mmW - 420) < 15 && Math.abs(mmH - 594) < 15) return 'A2';
  if (Math.abs(mmW - 297) < 15 && Math.abs(mmH - 420) < 15) return 'A3';
  if (Math.abs(mmW - 210) < 10 && Math.abs(mmH - 297) < 10) return 'A4';
  return `${mmW}×${mmH}mm`;
}

function renderSheetList() {
  const n = app.pageCount();
  const badge = $('#sheet-count-badge');
  if (badge) badge.textContent = `${n} bản vẽ`;

  const summary = $('#sheet-files-summary');
  if (summary) {
    if (!n) {
      summary.textContent = 'Chưa mở file';
    } else {
      const p1 = S.docs[1] ? `Ver1: ${S.docs[1].page_count} tr` : '';
      const p2 = S.docs[2] ? `Ver2: ${S.docs[2].page_count} tr` : '';
      summary.textContent = [p1, p2].filter(Boolean).join(' · ');
    }
  }

  const listEl = $('#sheet-list');
  if (!listEl) return;
  if (!n) {
    listEl.innerHTML = '<div class="sheet-empty">Chưa có bản vẽ nào.<br>Mở PDF 1 hoặc PDF 2 để hiển thị danh sách trang.</div>';
    return;
  }

  listEl.innerHTML = '';
  for (let p = 0; p < n; p++) {
    const item = document.createElement('div');
    item.className = `sheet-item ${p === S.page ? 'active' : ''}`;
    item.dataset.page = p;

    let w = 842, h = 595;
    for (const v of [1, 2]) {
      if (S.docs[v]?.pages?.[p]) {
        [w, h] = S.docs[v].pages[p];
        break;
      }
    }
    const sizeName = paperSizeName(w, h);
    const diffs = S.diffItems[p]?.length || 0;
    const diffHtml = diffs > 0 ? `<span class="sheet-item-badge diff">● ${diffs} khác biệt</span>` : '';

    item.innerHTML = `
      <div class="sheet-item-header">
        <span class="sheet-item-page"><b>#${p + 1}</b> Bản vẽ ${p + 1}</span>
        <span class="sheet-item-size">${sizeName}</span>
      </div>
      <div class="sheet-thumb-wrap">
        <img class="sheet-thumb-img" alt="" loading="lazy">
      </div>
      ${diffHtml}
    `;

    // Render thumbnail lazily
    const imgEl = item.querySelector('.sheet-thumb-img');
    const docVer = S.docs[1] ? 1 : 2;
    if (S.docs[docVer]) {
      api.renderPage(S.docs[docVer].id, p, 24).then(url => {
        imgEl.src = url;
      }).catch(() => {});
    }

    item.onclick = () => setPage(p);
    listEl.appendChild(item);
  }
}
app.renderSheetList = renderSheetList;

function updateSheetActive() {
  $$('.sheet-item').forEach(it => {
    const isAct = +it.dataset.page === S.page;
    it.classList.toggle('active', isAct);
    if (isAct) it.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  });
}

function setPage(p) {
  const n = app.pageCount();
  if (!n) return;
  p = Math.max(0, Math.min(n - 1, p));
  $('#inp-page').value = p + 1;
  if (p === S.page && A.hasImage) return;
  S.page = p;
  S.focus = null;
  updateSheetActive();
  refreshView();
}
app.setPage = setPage;

function setMode(mode) {
  S.mode = mode;
  $$('#mode-seg .seg-btn').forEach(b => b.classList.toggle('active', b.dataset.mode === mode));
  const side = mode === 'side';
  B.el.hidden = !side;
  $('#splitter').hidden = !side;
  if (side) { A.el.style.flex = '1 1 0'; B.el.style.flex = '1 1 0'; }
  else A.el.style.flex = '';
  refreshView();
  if (side) requestAnimationFrame(() => sync(A, B));
}
app.setMode = setMode;

function setTool(tool) {
  S.tool = tool;
  $$('#tool-seg .seg-btn').forEach(b => b.classList.toggle('active', b.dataset.tool === tool));
  for (const v of [A, B]) v.setTool(tool, tool === 'highlight' ? S.hlColor : TOOL_COL[tool]);
  if (tool === 'highlight') app.showTab('markup');
  app.msg(TOOL_HINT[tool], 8000);
}
app.setTool = setTool;
app.fit = () => { A.fit(); if (S.mode === 'side') sync(A, B); };

// ============================================================== rendering
function refreshView() {
  const dpi = app.dpiFor(S.page);
  const srcs = { v1: [[A, 1]], v2: [[A, 2]], side: [[A, 1], [B, 2]], overlay: [[A, 'ov']] }[S.mode];
  S.viewSrc = new Map(srcs);
  for (const [view, src] of srcs) show(view, src, dpi);
  redrawOverlays();
}
app.refreshView = refreshView;

function show(view, src, dpi) {
  const p = S.page;
  const st = S.set;
  if (src === 'ov') {
    view.setBadge('OVERLAY', `Trang ${p + 1} · Đỏ = chỉ Ver1 · Xanh = chỉ Ver2`, COL.orange);
    if (!app.both()) {
      view.hiresProvider = null;
      view.clear('Overlay cần mở cả Ver1 và Ver2 (cùng có trang này)');
      return;
    }
    const [w1, h1] = S.docs[1].pages[p], [w2, h2] = S.docs[2].pages[p];
    view.hiresProvider = (clip, need) => api.renderOverlay({
      d1: S.docs[1].id, d2: S.docs[2].id, page: p,
      thr: st.thr, tol: st.tol, dx: st.dx, dy: st.dy, dpi: need, clip
    });
    view.setImage({
      url: api.renderOverlay({
        d1: S.docs[1].id, d2: S.docs[2].id, page: p,
        thr: st.thr, tol: st.tol, dx: st.dx, dy: st.dy, dpi: dpi
      }),
      w: Math.max(w1, w2), h: Math.max(h1, h2), dpi
    }).catch(app.fail);
    return;
  }
  const doc = S.docs[src];
  const accent = src === 1 ? COL.blue : COL.green;
  if (!doc) {
    view.setBadge(`VER ${src}`, '', accent);
    view.hiresProvider = null;
    view.clear(`Kéo thả file PDF vào đây hoặc bấm 📂 PDF ${src}`);
    return;
  }
  view.setBadge(`VER ${src}`, `${doc.name} · Trang ${p + 1}/${doc.page_count}`, accent);
  if (p >= doc.page_count) {
    view.hiresProvider = null;
    view.clear(`Ver${src} không có trang ${p + 1}`);
    return;
  }
  const [w, h] = doc.pages[p];
  view.hiresProvider = (clip, need) => api.renderPage(doc.id, p, need, clip);
  view.setImage({
    url: api.renderPage(doc.id, p, dpi),
    w, h, dpi
  }).catch(app.fail);
}

// =============================================================== overlays
function redrawOverlays() {
  const p = S.page;
  const { toView, grow } = app;
  for (const [view, src] of S.viewSrc) {
    if (!view.hasImage) { view.setShapes([]); continue; }
    const sh = [];
    const rect = (r, color, fill, width, o = {}) => sh.push({ kind: 'rect', rect: r, color, fill, width, ...o });
    if (S.set.show) {
      for (const it of S.diffItems[p] || []) {
        if (src === 1 && (it.kind === 'added' || it.kind === 'geo_added')) continue;
        if (src === 2 && (it.kind === 'removed' || it.kind === 'geo_removed')) continue;
        const r = toView(it.rect, 1, src);
        if (it.kind.startsWith('geo')) rect(r, KIND_COL[it.kind], 14, 1.6, { dash: true, z: 8 });
        else rect(grow(r, 1), KIND_COL[it.kind], 70, 1.2, { z: 9 });
      }
    }
    for (const z of S.zones) {
      if (z.page !== p) continue;
      rect(toView(z.rect, 1, src), ZONE_COL[z.status || ''] || COL.purple, 22, 2.2,
        { label: z.name + (z.status ? ` · ${z.status}` : ''), z: 12 });
    }
    for (const h of S.queryHits) {
      if (h.page !== p || ((src === 1 || src === 2) && h.ver !== src)) continue;
      const col = h.changed ? (h.ver === 1 ? COL.red : COL.green) : S.queryColor;
      rect(grow(toView(h.rect, h.ver, src), 1.5), col, 90, 2, { z: 14 });
    }
    if (S.region && S.region.page === p) {
      rect(toView(S.region.rect, 1, src), COL.yellow, 8, 2, { label: 'Vùng đọc data', dash: true, z: 11 });
      const td = S.regionTd;
      if (td) {
        if (src === 1 || src === 'ov') for (const w of td.removed) rect(toView(w, 1, src), COL.red, 90, 1, { z: 13 });
        if (src === 2 || src === 'ov') for (const w of td.added) rect(toView(w, 2, src), COL.green, 90, 1, { z: 13 });
        for (const [a, b] of td.changed) {
          if (src === 1 || src === 'ov') rect(toView(a, 1, src), COL.orange, 90, 1, { z: 13 });
          if (src === 2) rect(b, COL.orange, 90, 1, { z: 13 });
        }
      }
    }
    for (const h of S.highlights) {
      if (h.page !== p || ((src === 1 || src === 2) && h.ver !== src)) continue;
      for (const r of h.rects) sh.push({ kind: 'hl', rect: toView(r, h.ver, src), color: h.color, opacity: h.opacity });
      const bb = toView(app.bbox(h.rects), h.ver, src);
      if (h.id === S.hlSel) rect(grow(bb, 2), darker(h.color, 1.6), 0, 1.5, { dash: true, z: 7 });
      if (h.comment) rect([bb[2] - 1, bb[1] - 1, bb[2] + 1, bb[1] + 1], darker(h.color, 1.3), 0, 1, { label: '💬', z: 7 });
    }
    if (S.focus && S.focus.page === p) {
      rect(grow(toView(S.focus.rect, S.focus.space, src), 3), COL.cyan, 0, 3, { z: 20 });
    }
    view.setShapes(sh);
  }
}
app.redrawOverlays = redrawOverlays;

app.goto = (page, r, space = 1, margin = 2.5) => {
  if (page !== S.page) setPage(page);
  const src = S.viewSrc.get(A) ?? 1;
  A.zoomTo(app.toView(r, space, src), margin);
  if (S.mode === 'side') sync(A, B);
  S.focus = { page, rect: r, space };
  redrawOverlays();
};

// ================================================================= events
let syncing = false;
function sync(src, dst) {
  if (syncing || S.mode !== 'side' || !src.hasImage || !dst.hasImage) return;
  syncing = true;
  try {
    const [cx, cy] = src.centerPt();
    const [dx, dy] = app.offset();
    const k = src === A ? 1 : -1;
    dst.setView(src.s, cx + k * dx, cy + k * dy);
  } finally { syncing = false; }
}

for (const v of [A, B]) {
  v.onViewChanged = view => {
    if (S.mode === 'side') sync(view, view === A ? B : A);
    $('#status-zoom').textContent = `Zoom: ${Math.round(view.s * 100)}%`;
  };
  v.onMouse = ([x, y]) => {
    $('#status-coord').textContent =
      `X: ${x.toFixed(1)}  Y: ${y.toFixed(1)} pt  (${(x * 25.4 / 72).toFixed(1)}, ${(y * 25.4 / 72).toFixed(1)} mm)`;
  };
  v.onRect = r => onRect(v, r);
  v.onContext = (pt, cx, cy) => app.onViewContext(v, pt, cx, cy);
}

function onRect(view, r) {
  const src = S.viewSrc.get(view) ?? 1;
  if (S.tool === 'highlight') { app.hlAddFromRect(view, r); return; }
  const r1 = app.rect2(app.toV1(r, src));
  if (S.tool === 'crop') doCrop(r1);
  else if (S.tool === 'read') {
    S.region = { page: S.page, rect: r1 };
    runRead();
    app.showTab('read');
  } else if (S.tool === 'zone') app.addZone(r1);
}

// ========================================================== settings UI
const settingsApplied = ui.debounce(() => {
  refreshView();
  if (S.region) runRead();
}, 350);

function bindSetting(id, key, conv, after) {
  const el = $(id);
  el.addEventListener(el.type === 'checkbox' ? 'change' : 'input', () => {
    const v = el.type === 'checkbox' ? el.checked : conv(el.value);
    if (el.type !== 'checkbox' && (v === null || Number.isNaN(v))) return;
    S.set[key] = v;
    after();
  });
}
const num = v => (v === '' ? null : +v);
bindSetting('#set-dpi', 'dpi', v => v, settingsApplied);
bindSetting('#set-thr', 'thr', v => { const n = num(v); return n === null ? n : Math.max(50, Math.min(254, Math.round(n))); }, settingsApplied);
bindSetting('#set-tol', 'tol', v => { const n = num(v); return n === null ? n : Math.max(0, Math.min(6, Math.round(n))); }, settingsApplied);
bindSetting('#set-dx', 'dx', num, settingsApplied);
bindSetting('#set-dy', 'dy', num, settingsApplied);
bindSetting('#set-pixel', 'pixel', null, () => {});
bindSetting('#set-text', 'text', null, () => {});
bindSetting('#set-show', 'show', null, redrawOverlays);

// ================================================================ compare
function needBoth() {
  if (!(S.docs[1] && S.docs[2])) { toast('Cần mở cả <b>Ver1</b> và <b>Ver2</b>.', 'warn'); return false; }
  return true;
}
app.needBoth = needBoth;

async function compareCurrent() {
  if (!needBoth()) return;
  const p = S.page;
  if (!app.both(p)) { toast(`Một trong hai bản không có trang ${p + 1}.`, 'warn'); return; }
  try {
    const r = await app.busy(() => api.postJSON('/api/compare', {
      ...app.settings(), page: p, dpi: app.dpiFor(p), pixel: S.set.pixel, text: S.set.text,
    }));
    S.diffItems[p] = r.items;
  } catch (e) { app.fail(e); return; }
  renderTree();
  redrawOverlays();
  renderSheetList();
  app.showTab('compare');
  app.msg(`Trang ${p + 1}: ${S.diffItems[p].length} khác biệt`);
  toast(`Trang ${p + 1}: <b>${S.diffItems[p].length}</b> khác biệt`, S.diffItems[p].length ? 'warn' : 'ok');
}

async function compareAll() {
  if (!needBoth()) return;
  const n = Math.min(S.docs[1].page_count, S.docs[2].page_count);
  const pg = ui.progress('So sánh tất cả trang', n);
  try {
    for (let p = 0; p < n; p++) {
      if (pg.cancelled) break;
      pg.set(p, `Đang so sánh trang ${p + 1}/${n}…`);
      const r = await api.postJSON('/api/compare', {
        ...app.settings(), page: p, dpi: Math.min(app.dpiFor(p), 100),
        pixel: S.set.pixel, text: S.set.text, use_cache: false,
      });
      S.diffItems[p] = r.items;
    }
  } catch (e) { app.fail(e); } finally { pg.close(); }
  renderTree();
  redrawOverlays();
  renderSheetList();
  app.showTab('compare');
}

const treeOpen = new Set();
function renderTree() {
  const el = $('#diff-tree');
  const pages = Object.keys(S.diffItems).map(Number).sort((a, b) => a - b);
  const tot = {};
  if (!pages.length) {
    el.innerHTML = '<div class="empty">Chưa có kết quả so sánh</div>';
    $('#sum-compare').innerHTML = 'Mở Ver1 và Ver2 rồi bấm So sánh.';
    app.setTabCount('compare', 0);
    return;
  }
  const html = [];
  let total = 0;
  for (const p of pages) {
    const items = S.diffItems[p];
    total += items.length;
    const open = treeOpen.has(p) || p === S.page || pages.length === 1;
    html.push(`<div class="tree-group ${open ? 'open' : ''}" data-p="${p}">
      <div class="tree-page"><span class="caret">▶</span>Trang ${p + 1}
      <span class="cnt" style="color:${items.length ? COL.orange : COL.green}">${items.length} khác biệt</span></div><div class="tree-items">`);
    items.forEach((it, i) => {
      tot[it.kind] = (tot[it.kind] || 0) + 1;
      html.push(`<div class="tree-item" data-p="${p}" data-i="${i}" style="--c:${KIND_COL[it.kind]}">
        <span class="kind">${esc(it.label)}</span><span class="txt" title="${esc(it.text)}">${esc(it.text)}</span>
        <span class="pos">${it.rect[0].toFixed(0)}, ${it.rect[1].toFixed(0)}</span></div>`);
    });
    html.push('</div></div>');
  }
  el.innerHTML = html.join('');
  const geo = (tot.geo_removed || 0) + (tot.geo_added || 0) + (tot.geo_changed || 0);
  $('#sum-compare').innerHTML =
    `<span class="k"><b>${pages.length}</b> trang đã so sánh</span>
     <span class="k" style="color:${COL.orange}">Nét vẽ: <b>${geo}</b> vùng</span><br>
     <span class="k" style="color:${COL.red}">Text xoá: <b>${tot.removed || 0}</b></span>
     <span class="k" style="color:${COL.green}">Text thêm: <b>${tot.added || 0}</b></span>
     <span class="k" style="color:${COL.orange}">Text sửa: <b>${tot.changed || 0}</b></span>`;
  app.setTabCount('compare', total);
}

$('#diff-tree').addEventListener('click', e => {
  const item = e.target.closest('.tree-item');
  if (item) {
    $$('.tree-item.sel').forEach(x => x.classList.remove('sel'));
    item.classList.add('sel');
    const p = +item.dataset.p, it = S.diffItems[p][+item.dataset.i];
    app.goto(p, it.rect, 1, 3.0);
    return;
  }
  const head = e.target.closest('.tree-page');
  if (head) {
    const g = head.parentElement, p = +g.dataset.p;
    g.classList.toggle('open');
    if (g.classList.contains('open')) treeOpen.add(p); else treeOpen.delete(p);
    if (p !== S.page) setPage(p);
  }
});

function exportDiffs() {
  const rows = [];
  for (const p of Object.keys(S.diffItems).map(Number).sort((a, b) => a - b)) {
    for (const it of S.diffItems[p]) rows.push([p + 1, it.label, it.text, ...it.rect.map(v => Math.round(v * 10) / 10)]);
  }
  if (!rows.length) { toast('Chưa có kết quả so sánh.', 'warn'); return; }
  api.excel('diff_report.xlsx', [{ title: 'Khac biet', headers: ['Trang', 'Loại', 'Nội dung', 'x0', 'y0', 'x1', 'y1'], rows, status_col: 1 }])
    .then(n => toast(`Đã xuất <b>${esc(n)}</b>`, 'ok')).catch(app.fail);
}

// ============================================================== read data
async function runRead() {
  if (!S.region) return;
  const { page, rect } = S.region;
  let r;
  try { r = await app.busy(() => api.postJSON('/api/read', { ...app.settings(), page, rect })); }
  catch (e) { app.fail(e); return; }
  S.regionTd = r.td;
  const both = r.has1 && r.has2;
  S.readRows = r.rows.map(([st, a, b]) => [both ? ROW_SYM[st] : '', a, b, st]);
  const cols = [both, r.has1, r.has2];
  const th = ['', 'Ver1', 'Ver2'];
  const tbl = $('#tbl-read');
  tbl.querySelector('thead').innerHTML = '<tr>' + th.map((t, i) => cols[i] ? `<th class="${i ? '' : 'c-sym'}">${t}</th>` : '').join('') + '</tr>';
  tbl.querySelector('tbody').innerHTML = S.readRows.length ? S.readRows.map(row => {
    const cls = both && row[3] !== 'equal' ? `st-${row[3]}` : '';
    return `<tr class="${cls}">` + row.slice(0, 3).map((t, i) => cols[i]
      ? `<td class="${i ? 'mono' : 'c-sym'}" title="${esc(t)}">${esc(t)}</td>` : '').join('') + '</tr>';
  }).join('') : '<tr><td colspan="3" class="empty">Không có text trong vùng</td></tr>';
  $('#read-preview').innerHTML = r.preview ? `<img src="${r.preview}" alt="Preview vùng đọc data">` : '<span class="muted">Không có preview</span>';
  const [x0, y0, x1, y1] = r.rect, w = x1 - x0, h = y1 - y0;
  const c = r.count || {};
  const px = r.px ? ` · Nét vẽ: <span style="color:${COL.red}">−${r.px.removed}</span> / <span style="color:${COL.green}">+${r.px.added}</span> px` : '';
  $('#sum-read').innerHTML =
    `Trang <b>${page + 1}</b> · vùng ${w.toFixed(0)}×${h.toFixed(0)} pt (${(w * 25.4 / 72).toFixed(0)}×${(h * 25.4 / 72).toFixed(0)} mm)<br>
     Ver1: <b>${r.n1 ?? '–'}</b> dòng &nbsp; Ver2: <b>${r.n2 ?? '–'}</b> dòng ·
     <span style="color:${COL.orange}">≠ ${c.changed || 0}</span>
     <span style="color:${COL.red}">− ${c.removed || 0}</span>
     <span style="color:${COL.green}">+ ${c.added || 0}</span>${px}`;
  redrawOverlays();
}
app.runRead = runRead;

const readTable = () => S.readRows.map(r => r.slice(0, 3));
$('#btn-read-copy').onclick = async () => {
  const rows = readTable();
  try {
    await navigator.clipboard.writeText([['', 'Ver1', 'Ver2'], ...rows].map(r => r.join('\t')).join('\n'));
    app.msg(`Đã copy ${rows.length} dòng vào clipboard`);
    toast(`Đã copy <b>${rows.length}</b> dòng`, 'ok');
  } catch (e) { app.fail(e); }
};
$('#btn-read-csv').onclick = () => {
  if (!S.readRows.length) return;
  ui.downloadText(ui.toCSV(['Trạng thái', 'Ver1', 'Ver2'], readTable()), 'region_data.csv', 'text/csv');
};
$('#btn-read-xlsx').onclick = () => {
  if (!S.readRows.length) return;
  api.excel('region_data.xlsx', [{ title: 'Doc data', headers: ['Trạng thái', 'Ver1', 'Ver2'], rows: readTable() }]).catch(app.fail);
};
$('#btn-read-zone').onclick = () => S.region && app.addZone(S.region.rect);
$('#btn-read-crop').onclick = () => S.region && doCrop(S.region.rect);

// =================================================================== crop
app.cropDialog = (has1, has2) => ui.modal({
  title: 'Crop vùng chọn',
  body: `
    <fieldset class="group"><legend>Nguồn</legend><div class="choice-col">
      <label class="chk"><input type="checkbox" id="cr-v1" ${has1 ? 'checked' : 'disabled'}><span>Ver1</span></label>
      <label class="chk"><input type="checkbox" id="cr-v2" ${has2 ? '' : 'disabled'} ${has2 && !has1 ? 'checked' : ''}><span>Ver2</span></label>
      <label class="chk"><input type="checkbox" id="cr-diff" ${has1 && has2 ? '' : 'disabled'}><span>Ảnh overlay khác biệt (PNG)</span></label>
    </div></fieldset>
    <fieldset class="group"><legend>Định dạng</legend><div class="choice-col">
      <label class="chk"><input type="radio" name="cr-fmt" id="cr-pdf" checked><span>PDF vector (giữ nét &amp; text)</span></label>
      <label class="chk"><input type="radio" name="cr-fmt" id="cr-png"><span>Ảnh PNG</span></label>
      <div class="form-grid wide" style="margin-top:4px"><label for="cr-dpi">Độ phân giải ảnh</label>
      <div class="suffix"><input type="number" id="cr-dpi" min="72" max="1200" value="300"><span>dpi</span></div></div>
    </div></fieldset>
    <div class="hint">Nhiều file sẽ được nén thành 1 file .zip.</div>`,
  buttons: [{ label: 'Huỷ', value: null }, { label: '✂ Xuất', value: true, primary: true }],
  collect: root => {
    const o = {
      use1: $('#cr-v1', root).checked, use2: $('#cr-v2', root).checked, use_diff: $('#cr-diff', root).checked,
      as_pdf: $('#cr-pdf', root).checked, dpi: Math.max(72, Math.min(1200, +$('#cr-dpi', root).value || 300)),
    };
    if (!o.use1 && !o.use2 && !o.use_diff) { toast('Chọn ít nhất 1 nguồn', 'warn'); return undefined; }
    return o;
  },
});

app.exportCrops = async (regions, o) => {
  try {
    const name = await app.busy(() => api.postDownload('/api/crop', { ...app.settings(), regions, ...o }, 'crop'));
    toast(`Đã xuất <b>${esc(name)}</b>`, 'ok');
  } catch (e) { app.fail(e); }
};

async function doCrop(r1) {
  const p = S.page, has1 = app.hasPage(1, p), has2 = app.hasPage(2, p);
  if (!has1 && !has2) return;
  const o = await app.cropDialog(has1, has2);
  if (!o) return;
  app.exportCrops([{ page: p, rect: r1, name: '' }], o);
}
app.doCrop = doCrop;

// ================================================================ toolbar
$('#btn-open1').onclick = () => openDoc(1);
$('#btn-open2').onclick = () => openDoc(2);
$$('#mode-seg .seg-btn').forEach(b => b.addEventListener('click', () => setMode(b.dataset.mode)));
$$('#tool-seg .seg-btn').forEach(b => b.addEventListener('click', () => setTool(b.dataset.tool)));
$('#btn-prev').onclick = () => setPage(S.page - 1);
$('#btn-next').onclick = () => setPage(S.page + 1);
$('#inp-page').addEventListener('change', e => setPage((+e.target.value || 1) - 1));
$('#btn-fit').onclick = app.fit;
$('#btn-zoom-in').onclick = () => A.zoomBy(1.25);
$('#btn-zoom-out').onclick = () => A.zoomBy(1 / 1.25);
$('#btn-compare').onclick = compareCurrent;
$('#btn-cmp-page').onclick = compareCurrent;
$('#btn-cmp-all').onclick = compareAll;
$('#btn-cmp-export').onclick = exportDiffs;

// -------------------------------------------------------------- shortcuts
document.addEventListener('keydown', e => {
  if (document.querySelector('.modal-back')) return;
  const t = e.target;
  const typing = t instanceof HTMLInputElement || t instanceof HTMLTextAreaElement || t instanceof HTMLSelectElement;
  const k = e.key, ctrl = e.ctrlKey || e.metaKey;
  if (typing && k !== 'Escape') return;
  const act = fn => { e.preventDefault(); fn(); };
  if (ctrl && (k === 'o' || k === 'O')) return act(() => openDoc(e.shiftKey ? 2 : 1));
  if (ctrl && k === '1') return act(() => openDoc(1));
  if (ctrl && k === '2') return act(() => openDoc(2));
  if (ctrl && (k === 'd' || k === 'D')) return act(compareCurrent);
  if (ctrl && (k === 'z' || k === 'Z')) return act(app.hlUndo);
  if (ctrl || e.altKey) return;
  const map = {
    PageUp: () => setPage(S.page - 1), PageDown: () => setPage(S.page + 1),
    f: app.fit, F: app.fit, h: () => setTool('pan'), H: () => setTool('pan'), Escape: () => { t.blur?.(); setTool('pan'); },
    c: () => setTool('crop'), C: () => setTool('crop'), r: () => setTool('read'), R: () => setTool('read'),
    Delete: () => {
      if (document.querySelector('#panel-zones.active') || S.tool === 'zone') {
        app.deleteZones?.();
      } else {
        app.hlDeleteSelected?.();
      }
    },
    '+': () => A.zoomBy(1.25), '=': () => A.zoomBy(1.25), '-': () => A.zoomBy(0.8),
    1: () => setMode('v1'), 2: () => setMode('v2'), 3: () => setMode('side'), 4: () => setMode('overlay'),
    b: toggleSheets, B: toggleSheets,
  };
  if (map[k]) act(map[k]);
});

// ----------------------------------------------------------- left sidebar toggle
function toggleSheets() {
  const sb = $('#left-sidebar');
  if (!sb) return;
  sb.classList.toggle('collapsed');
  $('#btn-toggle-sheets')?.classList.toggle('active', !sb.classList.contains('collapsed'));
}
$('#btn-toggle-sheets')?.addEventListener('click', toggleSheets);
$('#btn-collapse-left')?.addEventListener('click', toggleSheets);

// ------------------------------------------------------------ drag & drop
let dragDepth = 0;
const hasFiles = e => [...(e.dataTransfer?.types || [])].includes('Files');
window.addEventListener('dragenter', e => { if (!hasFiles(e)) return; dragDepth++; $('#drop-overlay').classList.add('show'); });
window.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; $('#drop-overlay').classList.remove('show'); } });
window.addEventListener('dragover', e => { if (hasFiles(e)) e.preventDefault(); });
window.addEventListener('drop', async e => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  dragDepth = 0;
  $('#drop-overlay').classList.remove('show');
  const files = [...e.dataTransfer.files].filter(f => /\.pdf$/i.test(f.name));
  if (!files.length) { toast('Chỉ hỗ trợ file PDF', 'warn'); return; }
  if (files.length >= 2) {
    files.sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }));
    await openDoc(1, files[0]);
    await openDoc(2, files[1]);
    return;
  }
  const f = files[0];
  if (!S.docs[1]) return openDoc(1, f);
  if (!S.docs[2]) return openDoc(2, f);
  const v = await ui.choice('Mở file', `Mở <b>${esc(f.name)}</b> làm:`, [
    { label: 'Huỷ', value: null }, { label: 'Ver1', value: 1, primary: true }, { label: 'Ver2', value: 2 },
  ]);
  if (v) openDoc(v, f);
});

// --------------------------------------------------------- resizers
function dragResize(handle, onMove) {
  handle.addEventListener('pointerdown', e => {
    e.preventDefault();
    handle.setPointerCapture(e.pointerId);
    handle.classList.add('drag');
    const move = ev => onMove(ev);
    const up = () => {
      handle.classList.remove('drag');
      handle.removeEventListener('pointermove', move);
      handle.removeEventListener('pointerup', up);
    };
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', up);
  });
}
dragResize($('#left-resizer'), e => {
  const w = Math.max(180, Math.min(innerWidth * 0.45, e.clientX));
  $('#left-sidebar').style.width = `${w}px`;
});
dragResize($('#dock-resizer'), e => {
  const w = Math.max(380, Math.min(innerWidth * 0.7, innerWidth - e.clientX));
  document.documentElement.style.setProperty('--dock-w', `${w}px`);
  localStorage.setItem('pdfqa.dockw', w);
});
dragResize($('#splitter'), e => {
  const b = $('#views').getBoundingClientRect();
  const f = Math.max(0.15, Math.min(0.85, (e.clientX - b.left) / b.width));
  A.el.style.flex = `${f} 1 0`;
  B.el.style.flex = `${1 - f} 1 0`;
});
const dw = localStorage.getItem('pdfqa.dockw');
if (dw) document.documentElement.style.setProperty('--dock-w', `${dw}px`);

window.addEventListener('beforeunload', e => {
  if (S.highlights.length) { e.preventDefault(); e.returnValue = ''; }
});

// ================================================================== init
installZones(app);
installQuery(app);
installMarkup(app);

(async function init() {
  try { Object.assign(app.meta, await api.getJSON('/api/meta')); }
  catch (e) { toast(`Không kết nối được server: ${esc(e.message)}`, 'error', 10000); }
  app.initMarkupAuthor?.();
  setMode('v1');
  setTool('pan');
  app.msg('Sẵn sàng. Mở hoặc kéo thả 1–2 file PDF để bắt đầu.', 10000);
})();
