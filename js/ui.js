// UI helpers: modals, toasts, progress, context menu, small DOM utils.

export const $ = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

export function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export function h(html) {
  const t = document.createElement('template');
  t.innerHTML = html.trim();
  return t.content.firstElementChild;
}

// ------------------------------------------------------------------ toasts
export function toast(msg, type = 'info', ms = 3800) {
  const col = { info: 'var(--accent)', ok: 'var(--green)', warn: 'var(--orange)', error: 'var(--red)' }[type];
  const el = h(`<div class="toast" style="--c:${col}"></div>`);
  el.innerHTML = msg;
  $('#toasts').appendChild(el);
  const kill = () => { el.classList.add('out'); setTimeout(() => el.remove(), 260); };
  setTimeout(kill, ms);
  el.addEventListener('click', kill);
}

// ------------------------------------------------------------------ modals
/**
 * Generic modal. body: HTML string or Node. buttons: [{label, value, primary, cls}]
 * onOpen(root) can wire inputs. collect(root, value) -> returned value (return undefined to keep open).
 */
export function modal({ title, body = '', buttons = [{ label: 'OK', value: true, primary: true }],
  wide = false, onOpen, collect, cancelValue = null }) {
  return new Promise(resolve => {
    const back = h(`<div class="modal-back"><div class="modal ${wide ? 'wide' : ''}" role="dialog" aria-modal="true">
      <div class="modal-head"><span>${esc(title)}</span><button class="x" title="Đóng">✕</button></div>
      <div class="modal-body"></div><div class="modal-foot"></div></div></div>`);
    const bodyEl = $('.modal-body', back);
    if (typeof body === 'string') bodyEl.innerHTML = body; else bodyEl.appendChild(body);
    const foot = $('.modal-foot', back);
    const done = v => {
      document.removeEventListener('keydown', onKey, true);
      back.remove();
      resolve(v);
    };
    const finish = v => {
      if (collect && v !== cancelValue) {
        const r = collect(back, v);
        if (r === undefined) return;
        done(r);
      } else done(v);
    };
    for (const b of buttons) {
      const btn = h(`<button class="btn ${b.primary ? 'primary' : ''} ${b.cls || ''}">${esc(b.label)}</button>`);
      btn.addEventListener('click', () => finish(b.value));
      foot.appendChild(btn);
    }
    $('.x', back).addEventListener('click', () => done(cancelValue));
    back.addEventListener('mousedown', e => { if (e.target === back) back._down = true; });
    back.addEventListener('mouseup', e => { if (e.target === back && back._down) done(cancelValue); back._down = false; });
    const onKey = e => {
      if (e.key === 'Escape') { e.stopPropagation(); done(cancelValue); }
      else if (e.key === 'Enter' && !(e.target instanceof HTMLTextAreaElement) && !(e.target instanceof HTMLButtonElement)) {
        const p = buttons.find(b => b.primary);
        if (p) { e.preventDefault(); e.stopPropagation(); finish(p.value); }
      }
    };
    document.addEventListener('keydown', onKey, true);
    document.getElementById('modal-root').appendChild(back);
    onOpen && onOpen(back, done);
    const f = back.querySelector('input:not([type=checkbox]):not([type=radio]), textarea, select');
    (f || foot.querySelector('.primary'))?.focus();
  });
}

export function confirmBox(title, text, okLabel = 'Đồng ý') {
  return modal({
    title, body: `<div>${text}</div>`,
    buttons: [{ label: 'Huỷ', value: false }, { label: okLabel, value: true, primary: true }], cancelValue: false,
  });
}

export function choice(title, text, options) {
  // options: [{label, value, primary}]
  return modal({ title, body: `<div>${text}</div>`, buttons: options, cancelValue: null });
}

export function promptText(title, label, value = '', multiline = true) {
  const body = `<div class="hint">${label}</div>` + (multiline
    ? `<textarea rows="5" id="dlg-prompt">${esc(value)}</textarea>`
    : `<input type="text" id="dlg-prompt" value="${esc(value)}">`);
  return modal({
    title, body,
    buttons: [{ label: 'Huỷ', value: null }, { label: 'OK', value: true, primary: true }],
    collect: root => $('#dlg-prompt', root).value,
  });
}

// ---------------------------------------------------------------- progress
export function progress(title, total) {
  const st = { cancelled: false };
  const back = h(`<div class="modal-back"><div class="modal"><div class="modal-head"><span>${esc(title)}</span></div>
    <div class="modal-body"><div class="pg-label">Đang xử lý…</div><div class="bar"><div></div></div></div>
    <div class="modal-foot"><button class="btn">Huỷ</button></div></div></div>`);
  $('.btn', back).addEventListener('click', () => { st.cancelled = true; $('.pg-label', back).textContent = 'Đang huỷ…'; });
  document.getElementById('modal-root').appendChild(back);
  st.set = (i, label) => {
    $('.bar > div', back).style.width = `${Math.round(100 * i / Math.max(1, total))}%`;
    if (label) $('.pg-label', back).textContent = label;
  };
  st.close = () => back.remove();
  return st;
}

// ------------------------------------------------------------ context menu
let ctxEl = null;
export function closeContext() { ctxEl?.remove(); ctxEl = null; }
export function contextMenu(items, x, y) {
  closeContext();
  const build = list => {
    const m = h('<div class="ctx"></div>');
    for (const it of list) {
      if (it === '-') { m.appendChild(h('<div class="ctx-sep"></div>')); continue; }
      const row = h(`<div class="ctx-item">${it.html || esc(it.label)}</div>`);
      if (it.sub) {
        row.appendChild(h('<span class="arrow">›</span>'));
        const sub = build(it.sub);
        sub.classList.add('ctx-sub');
        row.appendChild(sub);
      } else {
        row.addEventListener('click', e => { e.stopPropagation(); closeContext(); it.action?.(); });
      }
      m.appendChild(row);
    }
    return m;
  };
  ctxEl = build(items);
  document.body.appendChild(ctxEl);
  const r = ctxEl.getBoundingClientRect();
  ctxEl.style.left = `${Math.min(x, innerWidth - r.width - 8)}px`;
  ctxEl.style.top = `${Math.min(y, innerHeight - r.height - 8)}px`;
}
document.addEventListener('mousedown', e => { if (ctxEl && !ctxEl.contains(e.target)) closeContext(); });
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeContext(); });

// ------------------------------------------------------------ files / misc
export function pickFile(input) {
  return new Promise(resolve => {
    input.value = '';
    input.onchange = () => resolve([...input.files]);
    input.oncancel = () => resolve([]);
    input.click();
  });
}

export function downloadBlob(blob, filename) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url; a.download = filename;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 4000);
}

export function downloadText(text, filename, mime = 'application/json') {
  downloadBlob(new Blob([text], { type: `${mime};charset=utf-8` }), filename);
}

export function readFileText(file) {
  return new Promise((res, rej) => {
    const fr = new FileReader();
    fr.onload = () => res(fr.result);
    fr.onerror = rej;
    fr.readAsText(file, 'utf-8');
  });
}

export function toCSV(headers, rows) {
  const q = v => {
    const s = String(v ?? '');
    return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  return '\ufeff' + [headers, ...rows].map(r => r.map(q).join(',')).join('\r\n');
}

export function debounce(fn, ms) {
  let t;
  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
}

export function now() {
  const d = new Date(), p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}
