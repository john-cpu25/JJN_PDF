// Zoomable / pannable page view with rubber-band selection and vector overlays.
// Stage coordinates == PDF display coordinates (points). The rendered PNG is
// laid out at 1 CSS px per point and the whole stage is CSS-transformed.
import { esc } from './ui.js';

const SVGNS = 'http://www.w3.org/2000/svg';

export class Viewer {
  constructor(el, { title = '', accent = '#4f8cff' } = {}) {
    this.el = el;
    this.title = title;
    this.subtitle = '';
    this.accent = accent;
    this.s = 1; this.tx = 0; this.ty = 0;
    this.pageW = 0; this.pageH = 0;
    this.hasImage = false;
    this.tool = 'pan';
    this.selColor = '#ffcc00';
    this.baseDpi = 100;
    this.hiresProvider = null;
    this.onRect = null; this.onViewChanged = null; this.onMouse = null; this.onContext = null;
    this._labels = [];
    this._hiresToken = 0;
    this._baseToken = 0;
    this._fitPending = false;
    this._build();
    this._events();
    this.setPlaceholder('Chưa mở file');
  }

  // ---------------------------------------------------------------- DOM
  _build() {
    this.el.tabIndex = 0;
    this.el.innerHTML = `
      <div class="stage" hidden>
        <div class="shadow"></div>
        <img class="base" alt="" draggable="false">
        <img class="hires" alt="" draggable="false" hidden>
        <svg class="hl-layer" xmlns="${SVGNS}"></svg>
        <svg class="ov-layer" xmlns="${SVGNS}"></svg>
      </div>
      <div class="labels"></div>
      <div class="sel-box" hidden></div>
      <div class="badge" hidden><i></i><span></span></div>
      <div class="placeholder">
        <div class="ph-card">
          <div class="ph-icon">📂</div>
          <div class="ph-title">Chưa mở bản vẽ PDF</div>
          <div class="ph-text">Kéo thả file PDF vào đây hoặc bấm chọn file:</div>
          <div class="ph-actions">
            <button class="btn primary ph-btn-v1" type="button">📂 Mở PDF 1 (Ver1)</button>
            <button class="btn ph-btn-v2" type="button">📂 Mở PDF 2 (Ver2)</button>
          </div>
          <div class="ph-hint">💡 Phím tắt: <b>Ctrl + O</b> (PDF 1) · <b>Ctrl + Shift + O</b> (PDF 2)</div>
        </div>
      </div>
      <div class="spinner" hidden></div>`;
    const q = s => this.el.querySelector(s);
    this.stage = q('.stage'); this.shadow = q('.shadow');
    this.base = q('img.base'); this.hires = q('img.hires');
    this.svgHl = q('svg.hl-layer'); this.svgOv = q('svg.ov-layer');
    this.labelsEl = q('.labels'); this.selBox = q('.sel-box');
    this.badge = q('.badge'); this.ph = q('.placeholder'); this.spinner = q('.spinner');
    this.ph.querySelector('.ph-btn-v1')?.addEventListener('click', (e) => {
      e.stopPropagation();
      document.querySelector('#btn-open1')?.click();
    });
    this.ph.querySelector('.ph-btn-v2')?.addEventListener('click', (e) => {
      e.stopPropagation();
      document.querySelector('#btn-open2')?.click();
    });
    this._lastSize = [this.el.clientWidth, this.el.clientHeight];
    new ResizeObserver(() => this._onResize()).observe(this.el);
  }

  setBadge(title, subtitle, accent) {
    if (title !== undefined) this.title = title;
    if (subtitle !== undefined) this.subtitle = subtitle;
    if (accent) this.accent = accent;
    this.badge.hidden = !this.title;
    this.badge.style.setProperty('--c', this.accent);
    this.badge.querySelector('span').innerHTML =
      `${esc(this.title)}${this.subtitle ? ` <small>· ${esc(this.subtitle)}</small>` : ''}`;
  }

  setPlaceholder(text, icon = '⇪') {
    this.ph.querySelector('.ph-text').textContent = text;
    this.ph.querySelector('.ph-icon').textContent = icon;
  }

  setLoading(on) { this.spinner.hidden = !on; }

  showProgress(text, frac) {
    let chip = this.el.querySelector('.progress-chip');
    if (!chip) {
      chip = document.createElement('div');
      chip.className = 'progress-chip';
      chip.innerHTML = '<div class="pc-t"></div><div class="bar"><div></div></div>';
      this.el.appendChild(chip);
    }
    chip.querySelector('.pc-t').textContent = text;
    chip.querySelector('.bar > div').style.width = `${Math.round((frac || 0) * 100)}%`;
  }

  hideProgress() { this.el.querySelector('.progress-chip')?.remove(); }

  // --------------------------------------------------------------- image
  /** Show page image. Returns a promise resolved once the bitmap is displayed. */
  async setImage({ url, w, h, dpi, keepView = true }) {
    const had = this.hasImage;
    const same = Math.abs(w - this.pageW) < 2 && Math.abs(h - this.pageH) < 2;
    this.pageW = w; this.pageH = h; this.baseDpi = dpi;
    this.hasImage = true;
    this.stage.hidden = false;
    this.ph.hidden = true;
    for (const e of [this.base, this.shadow]) { e.style.width = `${w}px`; e.style.height = `${h}px`; }
    const off = Math.max(w, h) * 0.004;
    this.shadow.style.transform = `translate(${off}px, ${off}px)`;
    for (const svg of [this.svgHl, this.svgOv]) {
      svg.setAttribute('width', w); svg.setAttribute('height', h);
      svg.setAttribute('viewBox', `0 0 ${w} ${h}`);
    }
    this._clearHires();
    if (!(keepView && had && same)) this.fit(); else this._apply();
    const token = ++this._baseToken;
    this.setLoading(true);
    let resolvedUrl;
    try {
      resolvedUrl = await Promise.resolve(url);
    } catch (e) {
      if (token === this._baseToken) this.setLoading(false);
      throw e;
    }
    return new Promise((resolve, reject) => {
      const img = new Image();
      img.onload = () => {
        if (token !== this._baseToken) return resolve(false);
        this.base.src = resolvedUrl;
        this.setLoading(false);
        this._scheduleHires();
        resolve(true);
      };
      img.onerror = () => {
        if (token !== this._baseToken) return resolve(false);
        this.setLoading(false);
        reject(new Error('Không render được trang'));
      };
      img.src = resolvedUrl;
    });
  }

  clear(placeholder) {
    this._baseToken++;
    this.hasImage = false;
    this.stage.hidden = true;
    this.base.removeAttribute('src');
    this._clearHires();
    this.setShapes([]);
    this.setLoading(false);
    this.ph.hidden = false;
    if (placeholder) this.setPlaceholder(placeholder);
  }

  // ----------------------------------------------------------- transform
  get vw() { return this.el.clientWidth; }
  get vh() { return this.el.clientHeight; }

  _apply(emit = true) {
    this.stage.style.transform = `translate(${this.tx}px, ${this.ty}px) scale(${this.s})`;
    this._placeLabels();
    if (emit) {
      this.onViewChanged?.(this);
      this._scheduleHires();
    }
  }

  fit() {
    if (!this.pageW) return;
    if (!this.vw || !this.vh) { this._fitPending = true; return; }
    this._fitPending = false;
    const W = this.pageW + 20, H = this.pageH + 20;
    this.s = Math.min(this.vw / W, this.vh / H);
    this.tx = (this.vw - this.pageW * this.s) / 2;
    this.ty = (this.vh - this.pageH * this.s) / 2;
    this._apply();
  }

  zoomAt(f, sx, sy) {
    const ns = Math.max(0.02, Math.min(80, this.s * f));
    f = ns / this.s;
    this.tx = sx - (sx - this.tx) * f;
    this.ty = sy - (sy - this.ty) * f;
    this.s = ns;
    this._apply();
  }

  zoomBy(f) { this.zoomAt(f, this.vw / 2, this.vh / 2); }

  zoomTo(r, margin = 1.5) {
    if (!r) return;
    const w = Math.max(r[2] - r[0], 30) * margin, hh = Math.max(r[3] - r[1], 30) * margin;
    const cx = (r[0] + r[2]) / 2, cy = (r[1] + r[3]) / 2;
    this.setView(Math.min(this.vw / w, this.vh / hh), cx, cy);
  }

  centerPt() { return [(this.vw / 2 - this.tx) / this.s, (this.vh / 2 - this.ty) / this.s]; }

  setView(s, cx, cy, emit = true) {
    this.s = Math.max(0.02, Math.min(80, s));
    this.tx = this.vw / 2 - cx * this.s;
    this.ty = this.vh / 2 - cy * this.s;
    this._apply(emit);
  }

  toPt(clientX, clientY) {
    const b = this.el.getBoundingClientRect();
    return [(clientX - b.left - this.tx) / this.s, (clientY - b.top - this.ty) / this.s];
  }

  _onResize() {
    const [ow, oh] = this._lastSize;
    const nw = this.vw, nh = this.vh;
    this._lastSize = [nw, nh];
    if (!nw || !nh || !this.hasImage) return;
    if (this._fitPending || !ow || !oh) { this.fit(); return; }
    const cx = (ow / 2 - this.tx) / this.s, cy = (oh / 2 - this.ty) / this.s;
    this.setView(this.s, cx, cy);
  }

  // ---------------------------------------------------------------- tools
  setTool(tool, color) {
    this.tool = tool;
    if (color) this.selColor = color;
    this.el.classList.toggle('tool-sel', tool !== 'pan');
  }

  _events() {
    const el = this.el;
    let pan = null, sel = null;

    el.addEventListener('wheel', e => {
      e.preventDefault();
      if (!this.hasImage) return;
      const unit = e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? 800 : 1;
      const f = Math.pow(1.0018, -e.deltaY * unit);
      const b = el.getBoundingClientRect();
      this.zoomAt(f, e.clientX - b.left, e.clientY - b.top);
    }, { passive: false });

    el.addEventListener('pointerdown', e => {
      el.focus({ preventScroll: true });
      if (!this.hasImage) return;
      if (e.button === 1 || (e.button === 0 && this.tool === 'pan')) {
        e.preventDefault();
        pan = { x: e.clientX, y: e.clientY };
        el.classList.add('panning');
        el.setPointerCapture(e.pointerId);
      } else if (e.button === 0) {
        e.preventDefault();
        const b = el.getBoundingClientRect();
        sel = { x0: e.clientX - b.left, y0: e.clientY - b.top, p0: this.toPt(e.clientX, e.clientY) };
        Object.assign(this.selBox.style, {
          left: `${sel.x0}px`, top: `${sel.y0}px`, width: '0px', height: '0px',
          borderColor: this.selColor, background: hexA(this.selColor, 0.16),
        });
        this.selBox.hidden = false;
        el.setPointerCapture(e.pointerId);
      }
    });

    el.addEventListener('pointermove', e => {
      if (this.hasImage) this.onMouse?.(this.toPt(e.clientX, e.clientY));
      if (pan) {
        this.tx += e.clientX - pan.x; this.ty += e.clientY - pan.y;
        pan = { x: e.clientX, y: e.clientY };
        this._apply();
      } else if (sel) {
        const b = el.getBoundingClientRect();
        const x = e.clientX - b.left, y = e.clientY - b.top;
        Object.assign(this.selBox.style, {
          left: `${Math.min(x, sel.x0)}px`, top: `${Math.min(y, sel.y0)}px`,
          width: `${Math.abs(x - sel.x0)}px`, height: `${Math.abs(y - sel.y0)}px`,
        });
      }
    });

    const end = e => {
      if (pan) { pan = null; el.classList.remove('panning'); }
      if (sel) {
        const p1 = this.toPt(e.clientX, e.clientY), p0 = sel.p0;
        sel = null;
        this.selBox.hidden = true;
        const r = [Math.max(0, Math.min(p0[0], p1[0])), Math.max(0, Math.min(p0[1], p1[1])),
          Math.min(this.pageW, Math.max(p0[0], p1[0])), Math.min(this.pageH, Math.max(p0[1], p1[1]))];
        if ((r[2] - r[0]) * this.s > 4 && (r[3] - r[1]) * this.s > 4) {
          this.onRect?.(r.map(v => Math.round(v * 100) / 100));
        }
      }
    };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);
    el.addEventListener('auxclick', e => e.button === 1 && e.preventDefault());
    el.addEventListener('contextmenu', e => {
      e.preventDefault();
      if (this.hasImage) this.onContext?.(this.toPt(e.clientX, e.clientY), e.clientX, e.clientY);
    });
  }

  // ------------------------------------------------------------- overlays
  /**
   * shapes: {kind:'rect', rect, color, fill(0-255), width, dash, label, z}
   *         {kind:'hl', rect, color, opacity}
   */
  setShapes(shapes) {
    const hl = [], ov = [];
    this._labels = [];
    const sorted = [...shapes].sort((a, b) => (a.z || 0) - (b.z || 0));
    for (const sh of sorted) {
      const [x0, y0, x1, y1] = sh.rect;
      const x = Math.min(x0, x1), y = Math.min(y0, y1), w = Math.abs(x1 - x0), hh = Math.abs(y1 - y0);
      if (sh.kind === 'hl') {
        hl.push(`<rect x="${x}" y="${y}" width="${w}" height="${hh}" fill="${sh.color}" fill-opacity="${Math.max(0.05, Math.min(1, sh.opacity))}"/>`);
        continue;
      }
      const fo = (sh.fill ?? 50) / 255;
      const dash = sh.dash ? ' stroke-dasharray="6 4"' : '';
      ov.push(`<rect x="${x}" y="${y}" width="${w}" height="${hh}" fill="${sh.color}" fill-opacity="${fo}" stroke="${sh.color}" stroke-width="${sh.width ?? 2}" vector-effect="non-scaling-stroke"${dash}/>`);
      if (sh.label) this._labels.push({ x, y, text: sh.label, color: sh.color });
    }
    this.svgHl.innerHTML = hl.join('');
    this.svgOv.innerHTML = ov.join('');
    this.labelsEl.innerHTML = this._labels.map(l =>
      `<div class="lbl" style="color:${l.color}">${esc(l.text)}</div>`).join('');
    this._placeLabels();
  }

  _placeLabels() {
    const nodes = this.labelsEl.children;
    for (let i = 0; i < nodes.length; i++) {
      const l = this._labels[i];
      nodes[i].style.left = `${l.x * this.s + this.tx}px`;
      nodes[i].style.top = `${l.y * this.s + this.ty}px`;
    }
  }

  // ---------------------------------------------------------------- hires
  _clearHires() {
    this._hiresToken++;
    this.hires.hidden = true;
    this.hires.removeAttribute('src');
  }

  _scheduleHires() {
    clearTimeout(this._hiresTimer);
    this._hiresTimer = setTimeout(() => this._doHires(), 180);
  }

  async _doHires() {
    if (!this.hasImage || !this.hiresProvider || !this.vw) return;
    let need = this.s * 72 * (window.devicePixelRatio || 1);
    if (need <= this.baseDpi * 1.15) { this._clearHires(); return; }
    need = Math.min(need, 1600);
    const [ax, ay] = [(0 - this.tx) / this.s, (0 - this.ty) / this.s];
    const [bx, by] = [(this.vw - this.tx) / this.s, (this.vh - this.ty) / this.s];
    const v = [Math.max(0, ax), Math.max(0, ay), Math.min(this.pageW, bx), Math.min(this.pageH, by)];
    if (v[2] <= v[0] || v[3] <= v[1]) return;
    if ((v[2] - v[0]) * (v[3] - v[1]) * (need / 72) ** 2 > 45e6) return;
    const url = await Promise.resolve(this.hiresProvider(v.map(n => Math.round(n * 100) / 100), Math.round(need)));
    if (!url) return;
    const token = ++this._hiresToken;
    const img = new Image();
    img.onload = () => {
      if (token !== this._hiresToken) return;
      this.hires.src = url;
      Object.assign(this.hires.style, {
        left: `${v[0]}px`, top: `${v[1]}px`, width: `${v[2] - v[0]}px`, height: `${v[3] - v[1]}px`,
      });
      this.hires.hidden = false;
    };
    img.src = url;
  }
}

export function hexA(hex, a) {
  const m = hex.replace('#', '');
  const n = parseInt(m.length === 3 ? m.split('').map(c => c + c).join('') : m, 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${a})`;
}
