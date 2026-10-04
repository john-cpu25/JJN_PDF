// Query tab: search text / numbers / regex across pages, zones or the read region.
export default function install(app) {
  const { S, ui, api, COL } = app;
  const { $, esc, toast } = ui;

  async function runQuery() {
    const pat = $('#q-text').value.trim();
    if (!pat) return;
    if (!(S.docs[1] || S.docs[2])) { toast('Chưa mở file PDF nào.', 'warn'); return; }
    const scope = +$('#q-scope').value;
    const vers = { 0: [1, 2], 1: [1], 2: [2] }[+$('#q-ver').value];
    let pages = scope === 0 ? [S.page] : [...Array(app.pageCount()).keys()];
    let rects = null;
    if (scope === 2) {
      rects = {};
      for (const z of S.zones) (rects[z.page] ||= []).push(z.rect);
      pages = Object.keys(rects).map(Number).sort((a, b) => a - b);
      if (!pages.length) { toast('Chưa có vùng check nào.', 'warn'); return; }
    } else if (scope === 3) {
      if (!S.region) { toast('Chưa có vùng Đọc data. Dùng công cụ 🔎 (R) để chọn.', 'warn'); return; }
      pages = [S.region.page];
      rects = { [S.region.page]: [S.region.rect] };
    }
    let r;
    try {
      r = await app.busy(() => api.postJSON('/api/query', {
        ...app.settings(), pattern: pat, vers, pages, rects,
        regex: $('#q-regex').checked, case: $('#q-case').checked, changed_only: $('#q-changed').checked,
      }));
    } catch (e) { app.fail(e); return; }
    S.queryHits = r.hits;
    render();
    app.redrawOverlays();
    if (!r.hits.length) toast('Không tìm thấy kết quả', 'info');
  }

  function render() {
    const hits = S.queryHits;
    $('#tbl-query tbody').innerHTML = hits.length ? hits.map((h, i) => {
      const st = h.changed ? (h.ver === 1 ? 'Xoá ở Ver2' : 'Mới ở Ver2') : 'Không đổi';
      const cls = h.changed ? (h.ver === 1 ? 'st-removed' : 'st-added') : '';
      return `<tr data-i="${i}" class="${cls}"><td>V${h.ver}</td><td>${h.page + 1}</td>
        <td class="mono" title="${esc(h.text)}">${esc(h.text)}</td><td>${st}</td>
        <td class="mono">${h.rect[0].toFixed(0)}, ${h.rect[1].toFixed(0)}</td></tr>`;
    }).join('') : '<tr><td colspan="5" class="empty">Không có kết quả</td></tr>';
    const nchg = hits.filter(h => h.changed).length;
    const npg = new Set(hits.map(h => h.page)).size;
    $('#sum-query').innerHTML = `Kết quả: <b>${hits.length}</b> trên <b>${npg}</b> trang &nbsp;·&nbsp; <span style="color:${COL.orange}">có thay đổi: <b>${nchg}</b></span>`;
    app.setTabCount('query', hits.length);
  }

  $('#tbl-query tbody').addEventListener('click', e => {
    const tr = e.target.closest('tr[data-i]');
    if (!tr) return;
    [...e.currentTarget.children].forEach(r => r.classList.toggle('sel', r === tr));
    const h = S.queryHits[+tr.dataset.i];
    if (S.mode === 'v1' && h.ver === 2) app.setMode('v2');
    else if (S.mode === 'v2' && h.ver === 1) app.setMode('v1');
    app.goto(h.page, h.rect, h.ver, 8.0);
  });

  function clear() {
    S.queryHits = [];
    $('#tbl-query tbody').innerHTML = '';
    $('#sum-query').textContent = 'Kết quả: —';
    app.setTabCount('query', 0);
    app.redrawOverlays();
  }

  function exportQuery() {
    if (!S.queryHits.length) return;
    const rows = S.queryHits.map(h => [`V${h.ver}`, h.page + 1, h.text, h.changed ? 'Thay đổi' : 'Không đổi',
      ...h.rect.map(v => Math.round(v * 10) / 10)]);
    api.excel('query_result.xlsx', [{ title: 'Query', headers: ['Bản', 'Trang', 'Text', 'Trạng thái', 'x0', 'y0', 'x1', 'y1'], rows, status_col: 3 }])
      .catch(app.fail);
  }

  $('#btn-q-run').onclick = runQuery;
  $('#q-text').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); runQuery(); } });
  $('#q-color').addEventListener('input', e => { S.queryColor = e.target.value; app.redrawOverlays(); });
  $('#btn-q-clear').onclick = clear;
  $('#btn-q-xlsx').onclick = exportQuery;
}
