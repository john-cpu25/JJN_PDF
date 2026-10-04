// Zone check (mini vùng) + batch multi-file check.
export default function install(app) {
  const { S, ui, api, ZONE_COL } = app;
  const { $, esc, toast } = ui;

  const HINTS = {
    compare: 'So sánh text + nét vẽ trong vùng giữa Ver1 và Ver2 → OK / CHANGED.',
    extract: 'Chỉ đọc & lưu text trong vùng (VD: số bản vẽ, rev, ngày) → xuất Excel. Dùng được với 1 bản.',
    contains: 'Text trong vùng (Ver2, nếu không có thì Ver1) phải chứa giá trị. VD: BACKDRAFTING',
    equals: 'Text trong vùng phải bằng chính xác giá trị.',
    regex: 'Text trong vùng phải khớp biểu thức. VD: ^L\\d+  hoặc  REV\\s*[B-Z]',
    not_empty: 'Vùng phải có text (không được để trống).',
    number_range: 'Mọi số trong vùng phải nằm trong khoảng. VD: 100-500',
  };
  const NO_EXP = ['compare', 'not_empty', 'extract'];
  const ruleName = r => app.meta.rules[r] || r;
  const pick = z => ({ name: z.name, page: z.page, rect: z.rect, rule: z.rule, expected: z.expected || '' });
  const allPages = () => $('#chk-allpages').checked;
  let sel = new Set();
  let anchor = null;
  let lastBatch = null;

  // ------------------------------------------------------------- dialog
  function zoneDialog({ name, rule = 'compare', expected = '', preview = '', allowCompare = true }) {
    if (!allowCompare && rule === 'compare') rule = 'extract';
    const opts = Object.entries(app.meta.rules).map(([k, v]) => {
      const dis = k === 'compare' && !allowCompare;
      return `<option value="${k}" ${k === rule ? 'selected' : ''} ${dis ? 'disabled' : ''}>${esc(v)}${dis ? '  (cần mở cả 2 bản)' : ''}</option>`;
    }).join('');
    return ui.modal({
      title: 'Định nghĩa vùng check',
      body: `<div class="form-grid wide">
          <label for="zd-name">Tên vùng</label><input type="text" id="zd-name" value="${esc(name)}">
          <label for="zd-rule">Quy tắc</label><select id="zd-rule">${opts}</select>
          <label for="zd-exp">Giá trị</label><input type="text" id="zd-exp" value="${esc(expected)}">
          <span></span><div class="hint" id="zd-hint"></div></div>
        ${preview ? `<div class="hint">Text hiện có trong vùng:</div><pre>${esc(preview)}</pre>` : ''}`,
      buttons: [{ label: 'Huỷ', value: null }, { label: 'OK', value: true, primary: true }],
      onOpen: root => {
        const upd = () => {
          const k = $('#zd-rule', root).value;
          $('#zd-hint', root).textContent = HINTS[k] || '';
          $('#zd-exp', root).disabled = NO_EXP.includes(k);
        };
        $('#zd-rule', root).addEventListener('change', upd);
        upd();
        $('#zd-name', root).select();
      },
      collect: root => ({
        name: $('#zd-name', root).value.trim() || 'Zone',
        rule: $('#zd-rule', root).value,
        expected: $('#zd-exp', root).value,
      }),
    });
  }

  // ------------------------------------------------------------- checks
  async function checkZones(list, onProgress) {
    const CH = 40;
    for (let i = 0; i < list.length; i += CH) {
      const chunk = list.slice(i, i + CH);
      const r = await api.postJSON('/api/zones/check', { ...app.settings(), zones: chunk.map(pick) });
      r.zones.forEach((z, k) => Object.assign(chunk[k], z));
      if (onProgress && onProgress(Math.min(list.length, i + CH), list.length) === false) break;
    }
  }

  const templates = () => {
    const seen = new Set(), out = [];
    for (const z of S.zones) if (!seen.has(z.name)) { seen.add(z.name); out.push(z); }
    return out;
  };

  function expandAllPages() {
    const n = app.pageCount();
    const exist = new Set(S.zones.map(z => `${z.name}\u0000${z.page}`));
    const temps = templates();
    let added = 0;
    for (const t of temps) {
      for (let p = 0; p < n; p++) {
        const key = `${t.name}\u0000${p}`;
        if (!exist.has(key)) {
          S.zones.push({ ...pick(t), page: p, status: '' });
          exist.add(key);
          added++;
        }
      }
    }
    const order = new Map(temps.map((t, i) => [t.name, i]));
    S.zones.sort((a, b) => a.page - b.page || (order.get(a.name) ?? 0) - (order.get(b.name) ?? 0));
    return added;
  }

  app.addZone = async r1 => {
    const p = S.page;
    const d = app.hasPage(2, p) ? S.docs[2] : S.docs[1];
    let preview = '';
    if (d && p < d.page_count) {
      const rr = d === S.docs[1] ? r1 : app.toView(r1, 1, 2);
      try { preview = (await api.postJSON('/api/text_in', { doc: d.id, page: p, rect: rr })).lines.join('\n'); }
      catch { /* ignore */ }
    }
    const v = await zoneDialog({
      name: `Z${String(S.zones.length + 1).padStart(2, '0')}`, preview,
      allowCompare: !!(S.docs[1] && S.docs[2]),
    });
    if (!v) return;
    const z = { name: v.name, page: p, rect: app.rect2(r1), rule: v.rule, expected: v.expected, status: '' };
    try { await app.busy(() => checkZones([z])); } catch (e) { app.fail(e); }
    S.zones.push(z);
    refreshZoneTable();
    app.redrawOverlays();
    app.showTab('zones');
  };

  async function runZoneCheck() {
    if (!S.zones.length) { toast('Chưa có vùng nào. Dùng công cụ <b>▣ Vùng check</b> (Z) để tạo.', 'warn'); return; }
    if (!(S.docs[1] || S.docs[2])) { toast('Chưa mở file PDF nào.', 'warn'); return; }
    if (allPages()) expandAllPages();
    const big = S.zones.length > 40;
    const pg = big ? ui.progress('Kiểm tra vùng', S.zones.length) : null;
    try {
      await app.busy(() => checkZones(S.zones, (i, n) => {
        pg?.set(i, `Đã kiểm tra ${i}/${n} vùng…`);
        return !pg?.cancelled;
      }));
    } catch (e) { app.fail(e); } finally { pg?.close(); }
    refreshZoneTable();
    app.redrawOverlays();
    const cnt = {};
    for (const z of S.zones) cnt[z.status || '—'] = (cnt[z.status || '—'] || 0) + 1;
    const npg = new Set(S.zones.map(z => z.page)).size;
    const txt = Object.entries(cnt).map(([k, v]) => `${k}: ${v}`).join(', ');
    app.msg(`Đã kiểm tra ${S.zones.length} vùng trên ${npg} trang — ${txt}`, 10000);
    toast(`Đã kiểm tra <b>${S.zones.length}</b> vùng trên <b>${npg}</b> trang<br>${esc(txt)}`, cnt.FAIL ? 'warn' : 'ok');
  }

  const pill = st => st ? `<span class="pill" style="--c:${ZONE_COL[st] || app.COL.grey}">${esc(st)}</span>` : '—';
  const oneLine = s => (s || '').replace(/\n/g, ' ⏎ ');

  function refreshZoneTable() {
    const tb = $('#tbl-zones tbody');
    const cnt = {};
    tb.innerHTML = S.zones.length ? S.zones.map((z, i) => {
      cnt[z.status || '—'] = (cnt[z.status || '—'] || 0) + 1;
      return `<tr data-i="${i}" class="${sel.has(i) ? 'sel' : ''}">
        <td title="${esc(z.name)}">${esc(z.name)}</td><td>${z.page + 1}</td>
        <td title="${esc(ruleName(z.rule))}">${esc(ruleName(z.rule))}</td><td title="${esc(z.expected)}">${esc(z.expected)}</td>
        <td>${pill(z.status)}</td><td title="${esc(z.detail)}">${esc(z.detail)}</td>
        <td class="c-t1 mono" title="${esc(z.text_v1)}">${esc(oneLine(z.text_v1))}</td>
        <td class="c-t2 mono" title="${esc(z.text_v2)}">${esc(oneLine(z.text_v2))}</td></tr>`;
    }).join('') : '<tr><td colspan="8" class="empty">Chưa có vùng nào</td></tr>';
    const parts = [`<b>${S.zones.length}</b> vùng`];
    for (const st of ['OK', 'CHANGED', 'FAIL', 'DATA', 'N/A']) {
      if (cnt[st]) parts.push(`<span style="color:${ZONE_COL[st]}">${st}: <b>${cnt[st]}</b></span>`);
    }
    $('#sum-zones').innerHTML = parts.join(' &nbsp;·&nbsp; ');
    const t = $('#tbl-zones');
    t.classList.toggle('hide-t1', !S.docs[1] && !!S.docs[2]);
    t.classList.toggle('hide-t2', !S.docs[2] && !!S.docs[1]);
    app.setTabCount('zones', S.zones.length);
  }
  app.refreshZoneTable = refreshZoneTable;

  const selRows = () => [...sel].filter(i => i < S.zones.length).sort((a, b) => a - b);

  $('#tbl-zones tbody').addEventListener('click', e => {
    const tr = e.target.closest('tr[data-i]');
    if (!tr) return;
    const i = +tr.dataset.i;
    if (e.shiftKey && anchor !== null) {
      sel = new Set();
      for (let k = Math.min(anchor, i); k <= Math.max(anchor, i); k++) sel.add(k);
    } else if (e.ctrlKey || e.metaKey) {
      sel.has(i) ? sel.delete(i) : sel.add(i);
      anchor = i;
    } else {
      sel = new Set([i]);
      anchor = i;
    }
    [...e.currentTarget.children].forEach(r => r.classList.toggle('sel', sel.has(+r.dataset.i)));
    const z = S.zones[i];
    app.goto(z.page, z.rect, 1, 1.6);
  });
  $('#tbl-zones tbody').addEventListener('dblclick', e => {
    const tr = e.target.closest('tr[data-i]');
    if (!tr) return;
    sel = new Set([+tr.dataset.i]);
    editZone();
  });

  async function editZone() {
    const rows = selRows();
    if (!rows.length) { toast('Chọn 1 vùng trong bảng', 'warn'); return; }
    const z = S.zones[rows[0]];
    const v = await zoneDialog({
      name: z.name, rule: z.rule, expected: z.expected, preview: z.text_v2 || z.text_v1,
      allowCompare: !!(S.docs[1] && S.docs[2]),
    });
    if (!v) return;
    const fam = allPages() ? S.zones.filter(x => x.name === z.name) : [z];
    for (const x of fam) Object.assign(x, v);
    try { await app.busy(() => checkZones(fam)); } catch (e) { app.fail(e); }
    refreshZoneTable();
    app.redrawOverlays();
  }

  function deleteZones() {
    const rows = selRows();
    if (!rows.length) { toast('Chọn vùng cần xoá (Ctrl/Shift để chọn nhiều)', 'warn'); return; }
    if (allPages()) {
      const names = new Set(rows.map(r => S.zones[r].name));
      S.zones = S.zones.filter(z => !names.has(z.name));
    } else {
      S.zones = S.zones.filter((_, i) => !sel.has(i));
    }
    sel = new Set();
    refreshZoneTable();
    app.redrawOverlays();
  }

  function saveTemplate() {
    if (!S.zones.length) return;
    const src = allPages() ? templates() : S.zones;
    const data = {
      app: 'PDF QA Viewer', version: 1, offset: app.offset(), all_pages: allPages(),
      zones: src.map(pick),
    };
    ui.downloadText(JSON.stringify(data, null, 2), 'qa_template.json');
    app.msg('Đã lưu template: qa_template.json');
  }

  async function loadTemplate() {
    const [f] = await ui.pickFile($('#file-template'));
    if (!f) return;
    let zones;
    try {
      const data = JSON.parse(await ui.readFileText(f));
      zones = (data.zones || []).map(d => ({
        name: d.name, page: +d.page, rect: d.rect.map(Number), rule: d.rule || 'compare', expected: d.expected || '', status: '',
      }));
    } catch (e) { toast(`File không hợp lệ: ${esc(e.message)}`, 'error'); return; }
    if (S.zones.length) {
      const r = await ui.choice('Template', 'Thay thế các vùng hiện có hay thêm vào?', [
        { label: 'Huỷ', value: null }, { label: 'Thêm vào', value: 'add' }, { label: 'Thay thế', value: 'replace', primary: true },
      ]);
      if (!r) return;
      if (r === 'replace') S.zones = [];
    }
    sel = new Set();
    S.zones.push(...zones);
    if (S.docs[1] || S.docs[2]) await runZoneCheck(); else refreshZoneTable();
    app.redrawOverlays();
    app.showTab('zones');
  }

  function exportZones() {
    if (!S.zones.length) return;
    const rows = S.zones.map(z => [z.name, z.page + 1, ruleName(z.rule), z.expected, z.status || '', z.detail || '',
      z.text_v1 || '', z.text_v2 || '', z.geo_changed_px || 0, ...z.rect.map(v => Math.round(v * 10) / 10)]);
    api.excel('zone_check.xlsx', [{
      title: 'Vung check', rows, status_col: 4,
      headers: ['Tên', 'Trang', 'Quy tắc', 'Giá trị', 'Kết quả', 'Chi tiết', 'Text Ver1', 'Text Ver2', 'Px hình khác', 'x0', 'y0', 'x1', 'y1'],
    }]).catch(app.fail);
  }

  function exportPivot() {
    if (!S.zones.length) return;
    const names = [...new Set(S.zones.map(z => z.name))];
    const pages = [...new Set(S.zones.map(z => z.page))].sort((a, b) => a - b);
    const cell = new Map(S.zones.map(z => [`${z.page}\u0000${z.name}`, z]));
    const rows = pages.map(p => {
      const st = [];
      const row = [p + 1];
      for (const nm of names) {
        const z = cell.get(`${p}\u0000${nm}`);
        row.push(z ? (z.text_v2 || z.text_v1 || '').replace(/\n/g, ' ') : '');
        if (z && (z.status === 'FAIL' || z.status === 'CHANGED')) st.push(`${nm}:${z.status}`);
      }
      row.push(st.join('; ') || 'OK');
      return row;
    });
    api.excel('zone_data.xlsx', [{
      title: 'Bang data', headers: ['Trang', ...names, 'Kiểm tra'], rows,
      status_col: names.length + 1, status_fallback: 'FFC7CE',
    }]).catch(app.fail);
  }

  async function cropZones() {
    const rows = selRows().length ? selRows() : S.zones.map((_, i) => i);
    if (!rows.length) return;
    const o = await app.cropDialog(!!S.docs[1], !!S.docs[2]);
    if (!o) return;
    app.exportCrops(rows.map(i => ({ page: S.zones[i].page, rect: S.zones[i].rect, name: S.zones[i].name })), o);
  }

  // -------------------------------------------------------- batch files
  async function batchCheck() {
    const temps = templates();
    if (!temps.length) {
      toast('Chưa có vùng nào. Mở 1 bản vẽ mẫu, dùng <b>▣ Vùng check</b> (Z) để tạo vùng (hoặc 📂 Mở template) rồi chạy lại.', 'warn', 7000);
      return;
    }
    const files = await ui.pickFile($('#file-batch'));
    if (!files.length) return;
    const pg = ui.progress('Kiểm tra nhiều file', files.length);
    const results = [];
    const tjson = JSON.stringify(temps.map(pick));
    for (let i = 0; i < files.length; i++) {
      if (pg.cancelled) break;
      const f = files[i];
      const label = `${i + 1}/${files.length}: ${f.name}`;
      pg.set(i, label);
      try {
        const r = await api.upload('/api/batch/file', { file: f, templates: tjson },
          fr => pg.set(i + fr * 0.6, fr < 1 ? `${label} — tải lên ${Math.round(fr * 100)}%` : `${label} — đang kiểm tra…`));
        for (const z of r.results) results.push({ name: r.name, doc: r.doc, z });
      } catch (e) {
        results.push({ name: f.name, doc: null, z: {
          name: '(file)', page: 0, rect: [0, 0, 0, 0], rule: 'extract', expected: '', status: 'FAIL',
          detail: `Không mở được: ${e.message}`, text_v1: '', text_v2: '',
        } });
      }
    }
    pg.set(files.length);
    pg.close();
    if (results.length) {
      lastBatch = { results, names: temps.map(t => t.name) };
      $('#btn-zone-batch-view').hidden = false;
      batchDialog(lastBatch);
    }
  }

  function batchDialog({ results, names }) {
    const cnt = {};
    for (const { z } of results) cnt[z.status] = (cnt[z.status] || 0) + 1;
    const nfiles = new Set(results.map(r => r.doc || r.name)).size;
    const parts = [`<b>${nfiles}</b> file · <b>${results.length}</b> vùng`];
    for (const st of ['OK', 'FAIL', 'DATA', 'N/A']) if (cnt[st]) parts.push(`<span style="color:${ZONE_COL[st]}">${st}: <b>${cnt[st]}</b></span>`);
    let rows = results;
    const body = ui.h(`<div style="display:flex;flex-direction:column;gap:10px;height:100%">
      <div class="summary">${parts.join(' &nbsp;·&nbsp; ')}</div>
      <div style="display:flex;align-items:center;gap:10px">
        <label class="muted" for="bt-filter">Lọc:</label>
        <select id="bt-filter" style="width:160px"><option value="">Tất cả</option><option>FAIL</option><option>OK</option><option>DATA</option><option>N/A</option></select>
        <span class="hint" style="margin-left:auto">Double-click 1 dòng để mở bản vẽ tại vùng đó</span>
      </div>
      <div class="table-wrap grow"><table class="tbl"><thead><tr><th>File</th><th>Trang</th><th>Vùng</th><th>Quy tắc</th><th>Kết quả</th><th>Text</th><th>Chi tiết</th></tr></thead><tbody></tbody></table></div>
    </div>`);
    const fill = () => {
      const f = $('#bt-filter', body).value;
      rows = results.filter(r => !f || r.z.status === f);
      $('tbody', body).innerHTML = rows.map(({ name, z }, i) => `<tr data-i="${i}">
        <td title="${esc(name)}">${esc(name)}</td><td>${z.page + 1}</td><td>${esc(z.name)}</td><td>${esc(ruleName(z.rule))}</td>
        <td>${pill(z.status)}</td><td class="mono" title="${esc(z.text_v1)}">${esc(oneLine(z.text_v1))}</td><td title="${esc(z.detail)}">${esc(z.detail)}</td></tr>`).join('')
        || '<tr><td colspan="7" class="empty">Không có dòng nào</td></tr>';
    };
    ui.modal({
      title: 'Kết quả kiểm tra nhiều file', wide: true, body,
      buttons: [{ label: '📊 Xuất Excel', value: 'xlsx', primary: true }, { label: 'Đóng', value: null }],
      onOpen: (root, close) => {
        fill();
        $('#bt-filter', root).addEventListener('change', fill);
        $('tbody', root).addEventListener('dblclick', async e => {
          const tr = e.target.closest('tr[data-i]');
          if (!tr) return;
          const r = rows[+tr.dataset.i];
          if (!r.doc) { toast('File này không mở được', 'warn'); return; }
          close(null);
          try {
            if (S.docs[1]?.id !== r.doc) {
              const info = await api.getJSON(`/api/docs/${r.doc}`);
              info.keep = true;
              app.setDoc(1, info);
            }
            if (S.mode !== 'side' || !S.docs[2]) app.setMode('v1');
            app.goto(r.z.page, r.z.rect, 1, 1.6);
          } catch (err) { app.fail(err); }
        });
      },
      collect: (_root, v) => {
        if (v === 'xlsx') { exportBatch(results, names); return undefined; }
        return v;
      },
    });
  }

  function exportBatch(results, names) {
    const sheet1 = results.map(({ name, z }) => [name, z.page + 1, z.name, ruleName(z.rule), z.expected || '', z.status, z.text_v1 || '', z.detail || '']);
    const grid = new Map();
    for (const { name, doc, z } of results) {
      const key = `${doc || name}\u0000${z.page}`;
      if (!grid.has(key)) grid.set(key, { name, page: z.page, zs: {} });
      grid.get(key).zs[z.name] = z;
    }
    const sheet2 = [...grid.values()].map(({ name, page, zs }) => {
      const bad = names.filter(n => zs[n] && zs[n].status === 'FAIL').map(n => `${n}:FAIL`);
      return [name, page + 1, ...names.map(n => (zs[n] ? (zs[n].text_v1 || '').replace(/\n/g, ' ') : '')), bad.join('; ') || 'OK'];
    });
    api.excel('batch_check.xlsx', [
      { title: 'Chi tiet', headers: ['File', 'Trang', 'Vùng', 'Quy tắc', 'Giá trị', 'Kết quả', 'Text', 'Chi tiết'], rows: sheet1, status_col: 5 },
      { title: 'Bang data', headers: ['File', 'Trang', ...names, 'Kiểm tra'], rows: sheet2, status_col: names.length + 2, status_fallback: 'FFC7CE' },
    ]).then(n => toast(`Đã xuất <b>${esc(n)}</b>`, 'ok')).catch(app.fail);
  }

  // --------------------------------------------------------------- wire
  const viewBtn = ui.h('<button class="btn" id="btn-zone-batch-view" hidden title="Mở lại bảng kết quả kiểm tra nhiều file gần nhất">📋 Kết quả nhiều file</button>');
  $('#btn-zone-crop').after(viewBtn);
  viewBtn.onclick = () => lastBatch && batchDialog(lastBatch);
  $('#btn-zone-run').onclick = runZoneCheck;
  $('#btn-zone-batch').onclick = batchCheck;
  $('#btn-zone-edit').onclick = editZone;
  $('#btn-zone-del').onclick = deleteZones;
  $('#btn-zone-save').onclick = saveTemplate;
  $('#btn-zone-load').onclick = loadTemplate;
  $('#btn-zone-xlsx').onclick = exportZones;
  $('#btn-zone-pivot').onclick = exportPivot;
  $('#btn-zone-crop').onclick = cropZones;
  refreshZoneTable();
}
