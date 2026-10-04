// Markup tab: Bluebeam-like highlights (snap to text lines or free area).
export default function install(app) {
  const { S, ui, api } = app;
  const { $, esc, toast } = ui;

  const SWATCHES = [['#ffe600', 'Vàng'], ['#7CFC00', 'Xanh lá'], ['#00e5ff', 'Xanh dương'],
    ['#ff6ec7', 'Hồng'], ['#ff9f1c', 'Cam'], ['#ff3b30', 'Đỏ']];

  // ------------------------------------------------------------ pen UI
  const sw = $('#hl-swatches');
  sw.innerHTML = SWATCHES.map(([c, n], i) =>
    `<button class="swatch ${i === 0 ? 'active' : ''}" data-c="${c}" title="${n}" style="background:${c}"></button>`).join('')
    + '<label class="color-pick" title="Màu khác"><input type="color" id="hl-custom" value="#ffe600"></label>';
  const setColor = c => {
    S.hlColor = c;
    if (S.tool === 'highlight') for (const v of [app.A, app.B]) v.setTool('highlight', c);
  };
  sw.addEventListener('click', e => {
    const b = e.target.closest('.swatch');
    if (!b) return;
    sw.querySelectorAll('.swatch').forEach(x => x.classList.toggle('active', x === b));
    setColor(b.dataset.c);
  });
  $('#hl-custom').addEventListener('input', e => {
    sw.querySelectorAll('.swatch').forEach(x => x.classList.remove('active'));
    setColor(e.target.value);
  });
  $('#hl-op').addEventListener('input', e => { $('#hl-op-lbl').textContent = `${e.target.value}%`; });
  $('#hl-author').addEventListener('change', e => localStorage.setItem('pdfqa.author', e.target.value.trim()));
  app.initMarkupAuthor = () => {
    $('#hl-author').value = localStorage.getItem('pdfqa.author') || app.meta.user || '';
  };

  // ------------------------------------------------------------ create
  app.hlAddFromRect = async (view, r) => {
    const src = S.viewSrc.get(view) ?? 1;
    const ver = src === 1 || src === 2 ? src : 1;
    const doc = S.docs[ver];
    if (!doc || !app.hasPage(ver)) return;
    const mode = +$('#hl-mode').value;
    let res;
    try { res = await api.postJSON('/api/snap', { doc: doc.id, page: S.page, rect: r, mode }); }
    catch (e) { app.fail(e); return; }
    if (!res.kind) { app.msg("Không có chữ trong vùng kéo (kiểu 'Chỉ tô chữ')."); toast("Không có chữ trong vùng kéo (kiểu 'Chỉ tô chữ').", 'warn'); return; }
    const h = {
      id: ++S.hlId, ver, page: S.page, rects: res.rects, color: S.hlColor, opacity: +$('#hl-op').value / 100,
      kind: res.kind, text: res.text, comment: '', author: $('#hl-author').value.trim(), date: ui.now(),
    };
    S.highlights.push(h);
    S.hlSel = h.id;
    refresh();
    app.redrawOverlays();
  };

  // ------------------------------------------------------------- table
  function refresh() {
    const tb = $('#tbl-markup tbody');
    tb.innerHTML = S.highlights.length ? S.highlights.map((h, i) => `<tr data-i="${i}" class="${h.id === S.hlSel ? 'sel' : ''}">
      <td><span class="sw-dot" style="background:${h.color}"></span></td><td>PDF ${h.ver}</td><td>${h.page + 1}</td>
      <td>${h.kind === 'text' ? 'Chữ' : 'Vùng'}</td><td class="mono" title="${esc(h.text)}">${esc(h.text.replace(/\n/g, ' ⏎ '))}</td>
      <td title="${esc(h.comment)}">${esc(h.comment.replace(/\n/g, ' '))}</td><td>${esc(h.author)}</td><td>${esc(h.date)}</td></tr>`).join('')
      : '<tr><td colspan="8" class="empty">Chưa có highlight</td></tr>';
    const cnt = {};
    for (const h of S.highlights) cnt[h.ver] = (cnt[h.ver] || 0) + 1;
    $('#sum-markup').innerHTML = `<b>${S.highlights.length}</b> highlight`
      + Object.keys(cnt).sort().map(v => ` &nbsp;·&nbsp; PDF ${v}: <b>${cnt[v]}</b>`).join('');
    app.setTabCount('markup', S.highlights.length);
    tb.querySelector('tr.sel')?.scrollIntoView({ block: 'nearest' });
  }
  app.refreshHlTable = refresh;

  const selected = () => S.highlights.find(h => h.id === S.hlSel) || null;

  $('#tbl-markup tbody').addEventListener('click', e => {
    const tr = e.target.closest('tr[data-i]');
    if (!tr) return;
    const h = S.highlights[+tr.dataset.i];
    S.hlSel = h.id;
    if ((S.mode === 'v2' && h.ver === 1) || (S.mode === 'v1' && h.ver === 2)) app.setMode(`v${h.ver}`);
    [...e.currentTarget.children].forEach(r => r.classList.toggle('sel', r === tr));
    app.goto(h.page, app.bbox(h.rects), h.ver, 2.5);
  });
  $('#tbl-markup tbody').addEventListener('dblclick', e => {
    const tr = e.target.closest('tr[data-i]');
    if (tr) comment(S.highlights[+tr.dataset.i]);
  });

  // ------------------------------------------------------------ actions
  function hitTest(view, [x, y]) {
    const src = S.viewSrc.get(view) ?? 1;
    for (let i = S.highlights.length - 1; i >= 0; i--) {
      const h = S.highlights[i];
      if (h.page !== S.page || ((src === 1 || src === 2) && h.ver !== src)) continue;
      for (const r of h.rects) {
        const v = app.grow(app.toView(r, h.ver, src), 1);
        if (x >= v[0] && x <= v[2] && y >= v[1] && y <= v[3]) return h;
      }
    }
    return null;
  }

  app.onViewContext = (view, pt, cx, cy) => {
    const h = hitTest(view, pt);
    if (!h) return;
    S.hlSel = h.id;
    refresh();
    app.redrawOverlays();
    const items = [
      { label: '💬 Ghi chú…', action: () => comment(h) },
      { label: '🎨 Đổi màu', sub: SWATCHES.map(([c, n]) => ({ html: `<span class="sw-dot" style="background:${c}"></span> ${n}`, action: () => recolor(h, c) })) },
      { label: '◐ Độ đậm', sub: [25, 45, 65, 85].map(o => ({ label: `${o}%`, action: () => { h.opacity = o / 100; app.redrawOverlays(); } })) },
    ];
    if (h.text) items.push({ label: '📋 Copy text', action: () => navigator.clipboard.writeText(h.text).then(() => toast('Đã copy text', 'ok')) });
    items.push('-', { label: '🗑 Xoá', action: () => remove(h) });
    ui.contextMenu(items, cx, cy);
  };

  async function comment(h) {
    if (!h) { toast('Chọn 1 highlight', 'warn'); return; }
    const txt = await ui.promptText('Ghi chú highlight', `PDF ${h.ver} · Trang ${h.page + 1}<br><span class="mono">${esc(h.text.slice(0, 120))}</span>`, h.comment);
    if (txt === null) return;
    h.comment = txt.trim();
    refresh();
    app.redrawOverlays();
  }

  function recolor(h, c) {
    if (!h) { toast('Chọn 1 highlight', 'warn'); return; }
    h.color = c;
    refresh();
    app.redrawOverlays();
  }

  function remove(h) {
    const i = S.highlights.indexOf(h);
    if (i >= 0) S.highlights.splice(i, 1);
    if (S.hlSel === h.id) S.hlSel = null;
    refresh();
    app.redrawOverlays();
  }

  app.hlDeleteSelected = () => { const h = selected(); if (h) remove(h); };
  app.hlUndo = () => {
    if (!S.highlights.length) return;
    remove(S.highlights[S.highlights.length - 1]);
    app.msg('Đã hoàn tác highlight cuối');
  };

  async function clearAll() {
    if (!S.highlights.length) return;
    if (!await ui.confirmBox('Xoá hết', `Xoá <b>${S.highlights.length}</b> highlight?`, 'Xoá hết')) return;
    S.highlights = [];
    S.hlSel = null;
    refresh();
    app.redrawOverlays();
  }

  async function savePdf() {
    if (!S.highlights.length) { toast('Chưa có highlight nào.', 'warn'); return; }
    const saved = [];
    for (const ver of [1, 2]) {
      const items = S.highlights.filter(h => h.ver === ver);
      const doc = S.docs[ver];
      if (!items.length || !doc) continue;
      try {
        const name = await app.busy(() => api.postDownload('/api/highlights/export', { doc: doc.id, items }, 'markup.pdf'));
        saved.push(`${esc(name)} (${items.length} highlight)`);
      } catch (e) { app.fail(e); }
    }
    if (saved.length) toast(`Highlight đã ghi thành annotation thật (mở được bằng Bluebeam / Acrobat):<br><b>${saved.join('<br>')}</b>`, 'ok', 7000);
  }

  function exportXlsx() {
    if (!S.highlights.length) return;
    const rows = S.highlights.map(h => [`PDF ${h.ver}`, h.page + 1, h.kind === 'text' ? 'Chữ' : 'Vùng', h.color, h.text, h.comment,
      h.author, h.date, ...app.bbox(h.rects).map(v => Math.round(v * 10) / 10)]);
    api.excel('markups.xlsx', [{ title: 'Markups', headers: ['Bản', 'Trang', 'Kiểu', 'Màu', 'Text', 'Ghi chú', 'Người', 'Ngày', 'x0', 'y0', 'x1', 'y1'], rows }])
      .catch(app.fail);
  }

  function saveJson() {
    if (!S.highlights.length) return;
    const data = {
      app: 'PDF QA Viewer', files: { 1: S.docs[1]?.name || '', 2: S.docs[2]?.name || '' },
      highlights: S.highlights,
    };
    ui.downloadText(JSON.stringify(data, null, 2), 'markups.json');
    app.msg('Đã lưu markup: markups.json');
  }

  async function loadJson() {
    const [f] = await ui.pickFile($('#file-markup'));
    if (!f) return;
    let items;
    try {
      const data = JSON.parse(await ui.readFileText(f));
      items = (data.highlights || []).map(d => ({
        ver: +d.ver, page: +d.page, rects: d.rects.map(r => r.map(Number)), color: d.color || '#ffe600',
        opacity: d.opacity ?? 0.45, kind: d.kind || 'text', text: d.text || '', comment: d.comment || '',
        author: d.author || '', date: d.date || '',
      }));
    } catch (e) { toast(`File không hợp lệ: ${esc(e.message)}`, 'error'); return; }
    for (const h of items) h.id = ++S.hlId;
    S.highlights.push(...items);
    refresh();
    app.redrawOverlays();
    app.showTab('markup');
    toast(`Đã mở <b>${items.length}</b> highlight`, 'ok');
  }

  $('#btn-hl-comment').onclick = () => comment(selected());
  $('#btn-hl-recolor').onclick = () => recolor(selected(), S.hlColor);
  $('#btn-hl-del').onclick = app.hlDeleteSelected;
  $('#btn-hl-clear').onclick = clearAll;
  $('#btn-hl-pdf').onclick = savePdf;
  $('#btn-hl-xlsx').onclick = exportXlsx;
  $('#btn-hl-save').onclick = saveJson;
  $('#btn-hl-load').onclick = loadJson;
  refresh();
}
