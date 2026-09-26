/* 翻译审核台 前端 —— 纯原生 JS，无构建、无 CDN（本机离线可用）。
 *
 * 一条设计上的硬规则：**改动词表只能经过「暂存 → 预览 → 保存」三步**。
 * 所有草稿都在浏览器内存里，只有点「保存」才会打到后端写盘，
 * 后端写盘前还会自己备份 + 复核不变量。前端不直接改文件。
 */

'use strict';

// ---------------------------------------------------------------------------
// 全局状态
// ---------------------------------------------------------------------------
const S = {
  meta: null,
  view: 'entries',
  filters: {
    q: '', kinds: new Set(), flags: new Set(), scope: '',
    sort: 'file', offset: 0, limit: 200,
  },
  rows: [],
  total: 0,
  grandTotal: 0,
  scopes: [],
  selected: null,
  detail: null,
  pending: { edits: {}, adds: {}, deletes: new Set() },
  draftDirty: false,
};

const PLACEHOLDER = '\u2401';      // ␁ = U+0001 的可视化替身（原文字符占位符）

// ---------------------------------------------------------------------------
// 小工具
// ---------------------------------------------------------------------------
const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));

function esc(text) {
  return String(text == null ? '' : text)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/** 把控制字符占位符换成可见字形，只在**显示**时用。 */
function disp(text) {
  return String(text == null ? '' : text).replace(/\u0001/g, PLACEHOLDER);
}
/** 显示值 → 真实值（编辑器里允许用户看到并保留 ␁）。 */
function undisp(text) {
  return String(text == null ? '' : text).replace(/\u2401/g, '\u0001');
}
function countPh(text) {
  const m = String(text == null ? '' : text).match(/\u0001|\u2401/g);
  return m ? m.length : 0;
}
/** 显示宽度：CJK 全角算 2，其余算 1（与后端同一口径）。 */
function width(text) {
  let w = 0;
  for (const ch of String(text == null ? '' : text)) {
    const cp = ch.codePointAt(0);
    w += (cp >= 0x1100 && (cp <= 0x115f || cp === 0x2329 || cp === 0x232a ||
      (cp >= 0x2e80 && cp <= 0xa4cf) || (cp >= 0xac00 && cp <= 0xd7a3) ||
      (cp >= 0xf900 && cp <= 0xfaff) || (cp >= 0xfe30 && cp <= 0xfe6f) ||
      (cp >= 0xff00 && cp <= 0xff60) || (cp >= 0xffe0 && cp <= 0xffe6))) ? 2 : 1;
  }
  return w;
}

let toastTimer = null;
function toast(message, kind) {
  const el = $('#toast');
  el.textContent = message;
  el.className = 'toast show' + (kind ? ' ' + kind : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.className = 'toast'; }, kind === 'bad' ? 6000 : 2600);
}

async function api(path, options) {
  const response = await fetch(path, options);
  let body = null;
  try { body = await response.json(); } catch (e) { /* 非 JSON */ }
  if (!response.ok || (body && body.error)) {
    const message = (body && body.error) || ('HTTP ' + response.status);
    toast('请求失败：' + message, 'bad');
    throw new Error(message);
  }
  return body;
}

function post(path, payload) {
  return api(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload || {}),
  });
}

function highlight(text, needle) {
  const safe = esc(disp(text));
  if (!needle) return safe;
  const pattern = esc(needle).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  try {
    return safe.replace(new RegExp(pattern, 'gi'), (m) => '<mark>' + m + '</mark>');
  } catch (e) { return safe; }
}

const KIND_ORDER = ['plain', 'scoped', 'template', 'prefix', 'suffix', 'middle'];

// ---------------------------------------------------------------------------
// 载入
// ---------------------------------------------------------------------------
async function boot() {
  S.meta = await api('/api/meta');
  renderMeta();
  renderSide();
  await loadEntries();
  await loadInconsistency();
  await loadAudit();
  loadRuntime();
  renderHelp();
  wire();
}

function renderMeta() {
  const m = S.meta;
  $('#brandSub').textContent = m.data_dir;
  $('#pillTable').textContent =
    '词表 ' + m.total + ' 条 · md5 ' + String(m.table_md5).slice(0, 10) + ' · ' + m.table_mtime;
  const pill = $('#pillPlugin');
  pill.textContent = m.plugin_dir_exists ? '运行目录就绪' : '运行目录不存在';
  pill.className = 'pill ' + (m.plugin_dir_exists ? 'ok' : 'bad');
  pill.title = m.plugin_dir;

  const counts = m.counts || {};
  $('#kindChips').innerHTML = KIND_ORDER
    .filter((k) => counts[k])
    .map((k) => '<span class="chip" data-kind="' + k + '">' +
      esc(m.kind_labels[k] || k) + '<span class="n">' + counts[k] + '</span></span>')
    .join('');

  const flags = m.flag_meta || {};
  const flagCounts = m.flag_counts || {};
  $('#flagChips').innerHTML = Object.keys(flags)
    .sort((a, b) => ({ high: 0, mid: 1, low: 2 }[flags[a].level] -
                      { high: 0, mid: 1, low: 2 }[flags[b].level]))
    .map((f) => {
      const info = flags[f];
      const n = flagCounts[f] || 0;
      if (!n) return '';
      return '<span class="chip ' + info.level + '" data-flag="' + f +
        '" title="' + esc(info.tip) + '">' + esc(info.label) +
        '<span class="n">' + n + '</span></span>';
    }).join('');

  const run = m.runtime || {};
  $('#tabRuntimeCount').textContent =
    (run.missing == null && run.untranslated == null) ? '' :
      (run.missing || 0) + (run.untranslated || 0);

  const problems = m.load_problems || [];
  if (problems.length) {
    const box = document.createElement('div');
    box.className = 'warn';
    box.style.margin = '8px 16px';
    box.innerHTML = '<b>词表结构异常（可能是本工具写坏了，也可能是手工编辑引入）：</b><br>' +
      problems.map(esc).join('<br>') + '<br>先用 git 对比并恢复词表，再继续审核。';
    const main = document.querySelector('main');
    main.parentNode.insertBefore(box, main);
  }
}

function renderSide() {
  const pal = $('#scopeSel');
  const keep = pal.value;
  pal.innerHTML = '<option value="">全部</option>' +
    S.scopes.map((s) => '<option value="' + esc(s) + '">' + esc(s) + '</option>').join('');
  pal.value = keep || S.filters.scope;

  $$('#kindChips .chip').forEach((chip) => {
    chip.classList.toggle('on', S.filters.kinds.has(chip.dataset.kind));
  });
  $$('#flagChips .chip').forEach((chip) => {
    chip.classList.toggle('on', S.filters.flags.has(chip.dataset.flag));
  });
}

function queryString() {
  const params = new URLSearchParams();
  if (S.filters.q) params.set('q', S.filters.q);
  if (S.filters.kinds.size) params.set('kind', Array.from(S.filters.kinds).join(','));
  if (S.filters.flags.size) params.set('flag', Array.from(S.filters.flags).join(','));
  if (S.filters.scope) params.set('scope', S.filters.scope);
  params.set('sort', S.filters.sort);
  params.set('offset', S.filters.offset);
  params.set('limit', S.filters.limit);
  return params.toString();
}

async function loadEntries() {
  const data = await api('/api/entries?' + queryString());
  S.rows = data.rows;
  S.total = data.total;
  S.grandTotal = data.grand_total;
  S.scopes = data.scopes || S.scopes;
  renderSide();
  renderGrid();
}

// ---------------------------------------------------------------------------
// 词条表
// ---------------------------------------------------------------------------
function renderGrid() {
  const from = S.total ? S.filters.offset + 1 : 0;
  const to = Math.min(S.filters.offset + S.filters.limit, S.total);
  $('#listInfo').textContent =
    '命中 ' + S.total + ' 条（全表 ' + S.grandTotal + '），显示 ' + from + '–' + to;
  const pages = Math.max(1, Math.ceil(S.total / S.filters.limit));
  const page = Math.floor(S.filters.offset / S.filters.limit) + 1;
  $('#pageInfo').textContent = page + ' / ' + pages;
  $('#btnPrev').disabled = S.filters.offset <= 0;
  $('#btnNext').disabled = to >= S.total;

  const flagMeta = (S.meta && S.meta.flag_meta) || {};
  const body = $('#gridBody');
  body.innerHTML = S.rows.map((row) => {
    const isEdit = Object.prototype.hasOwnProperty.call(S.pending.edits, row.key);
    const isDel = S.pending.deletes.has(row.key);
    const classes = [];
    if (row.key === S.selected) classes.push('sel');
    if (isEdit) classes.push('dirty');
    if (isDel) classes.push('todelete');
    const tags = (row.flags || []).map((f) => {
      const info = flagMeta[f] || { label: f, level: 'low', tip: '' };
      return '<span class="tag ' + info.level + '" title="' + esc(info.tip) + '">' +
        esc(info.label) + '</span>';
    }).join('');
    const show = isEdit ? S.pending.edits[row.key] : row.value;
    const wUp = row.w_dst > Math.max(row.w_src * 1.6, 8) ? ' up' : '';
    return '<tr data-key="' + esc(row.key) + '" class="' + classes.join(' ') + '">' +
      '<td class="mono">' + row.line + '</td>' +
      '<td><span class="kind ' + row.kind + '">' +
        esc((S.meta.kind_labels || {})[row.kind] || row.kind) + '</span></td>' +
      '<td class="mono">' + esc(row.scope || '') + '</td>' +
      '<td><div class="trunc srcText">' + highlight(row.plain, S.filters.q) + '</div></td>' +
      '<td><div class="trunc dstText">' + highlight(show, S.filters.q) + '</div></td>' +
      '<td class="wcell' + wUp + '">' + row.w_src + '→' + width(show) + '</td>' +
      '<td>' + tags + '</td>' +
      '</tr>';
  }).join('') || '<tr><td colspan="7" style="padding:30px;text-align:center;color:var(--text3)">没有命中的词条。放宽筛选试试。</td></tr>';
}

async function selectRow(key) {
  if (S.draftDirty && !confirm('当前编辑还没暂存，切换会丢掉它。继续？')) return;
  S.selected = key;
  S.draftDirty = false;
  renderGrid();
  const detail = await api('/api/detail?key=' + encodeURIComponent(key));
  S.detail = detail;
  renderDetail();
}

function renderDetail() {
  const d = S.detail;
  const host = $('#detail');
  if (!d) { host.innerHTML = '<div class="empty">点击左侧任一行查看详情并编辑。</div>'; return; }

  const flagMeta = (S.meta && S.meta.flag_meta) || {};
  const isDel = S.pending.deletes.has(d.key);
  const staged = Object.prototype.hasOwnProperty.call(S.pending.edits, d.key)
    ? S.pending.edits[d.key] : null;
  const current = staged != null ? staged : d.value;

  const tags = (d.flags || []).map((f) => {
    const info = flagMeta[f] || { label: f, level: 'low', tip: '' };
    return '<span class="tag ' + info.level + '" title="' + esc(info.tip) + '">' +
      esc(info.label) + '</span>';
  }).join('') || '<span class="hint">无风险标签</span>';

  const sibs = (d.siblings || []).map((s) => {
    const same = s.key === d.key;
    return '<div class="sib' + (same ? ' cur' : '') + '" data-key="' + esc(s.key) + '">' +
      '<span class="sk">' + esc(disp(s.plain)) + '</span>' +
      '<span class="sv">' + esc(disp(s.value)) + '</span>' +
      '<span class="sw">' + s.w_src + '→' + s.w_dst + '</span></div>';
  }).join('') || '<span class="hint">—</span>';

  const phSrc = countPh(d.plain);
  const phDst = countPh(current);

  host.innerHTML =
    '<h3>' +
      '<span class="kind ' + d.kind + '">' +
        esc((S.meta.kind_labels || {})[d.kind] || d.kind) + '</span>' +
      (isDel ? '<span class="tag high">待删除</span>' : '') +
    '</h3>' +
    '<div class="dgroup"><label>键（第 ' + d.line + ' 行' +
      (d.scope ? ' · 作用域 ' + esc(d.scope) : '') + '）</label>' +
      '<div class="keyBox">' + esc(disp(d.key)) + '</div></div>' +

    '<div class="dgroup"><label>原文' +
      (phSrc ? ' （含 ' + phSrc + ' 个占位符 ' + PLACEHOLDER + '）' : '') + '</label>' +
      '<div class="origBox">' + esc(disp(d.plain)) + '</div></div>' +

    '<div class="dgroup"><label>译文' +
      (phDst ? ' （含 ' + phDst + ' 个占位符 ' + PLACEHOLDER + '）' : '') + '</label>' +
      '<textarea class="edit" id="editBox" spellcheck="false">' +
        esc(disp(current)) + '</textarea>' +
      '<div class="metaRow">' +
        '<span>宽度 <b>' + width(d.plain) + '</b> → <b>' +
          '<span class="' + (width(current) > Math.max(width(d.plain) * 1.6, 8) ? 'warnInline' : '') +
          '">' + width(current) + '</b></span>' +
        '<span>字符 <b>' + current.length + '</b></span>' +
        (phDst > phSrc
          ? '<span class="warnInline">⚠ 占位符比原文多（原文 ' + phSrc + ' 个）：标签回填会错位</span>'
          : (phDst < phSrc && phSrc
              ? '<span class="hint">占位符比原文少 ' + (phSrc - phDst) +
                ' 个（若改写成字面 &lt;b&gt; 标签则正常）</span>'
              : '')) +
      '</div>' +
      '<div class="row2">' +
        '<button class="btn tiny primary" id="btnStage">暂存改动 (Ctrl+Enter)</button>' +
        '<button class="btn tiny ghost" id="btnRevert">还原</button>' +
      '</div>' +
      '<div class="row2">' +
        '<button class="btn tiny ghost" id="btnCopySrc">复制原文</button>' +
        '<button class="btn tiny ghost" id="btnDelete">' + (isDel ? '撤销删除' : '删除此键') + '</button>' +
      '</div>' +
    '</div>' +

    '<div class="dgroup"><label>风险标签</label><div>' + tags + '</div></div>' +

    '<div class="dgroup"><label>同组键（' + (d.group_size || 0) + ' 条，' +
      (d.kind === 'plain' ? '取文件顺序上的邻居' : '同作用域 / 同类') +
      ' —— 短标签照这一组对齐长度）</label><div>' + sibs + '</div></div>';

  const box = $('#editBox');
  box.addEventListener('input', () => {
    const dirty = undisp(box.value) !== current;
    S.draftDirty = dirty;
    box.classList.toggle('dirty', dirty);
  });
  $('#btnStage').addEventListener('click', stageDraft);
  $('#btnRevert').addEventListener('click', () => renderDetail());
  $('#btnCopySrc').addEventListener('click', async () => {
    try { await navigator.clipboard.writeText(d.plain); toast('原文已复制', 'ok'); }
    catch (e) { toast('复制失败，请手动选择', 'bad'); }
  });
  $('#btnDelete').addEventListener('click', () => toggleDelete(d.key));
  $$('#detail .sib').forEach((el) => {
    el.addEventListener('click', () => selectRow(el.dataset.key));
  });
}

function stageDraft() {
  const box = $('#editBox');
  if (!box || !S.detail) return;
  const value = undisp(box.value);
  const key = S.detail.key;
  if (value === S.detail.value) delete S.pending.edits[key];
  else S.pending.edits[key] = value;
  S.draftDirty = false;
  renderPending();
  renderDetail();
  renderGrid();
  toast('已暂存：' + (value === S.detail.value ? '与原文相同，已撤销暂存' : '待保存'), 'ok');
}

function toggleDelete(key) {
  if (S.pending.deletes.has(key)) S.pending.deletes.delete(key);
  else {
    if (!confirm('把这条从词表里删掉？保存后可在 data/.backups/ 里找回。\n\n键：' + key)) return;
    S.pending.deletes.add(key);
    delete S.pending.edits[key];
  }
  renderPending();
  renderDetail();
  renderGrid();
}

function renderPending() {
  const { edits, adds, deletes } = S.pending;
  const editKeys = Object.keys(edits);
  const addKeys = Object.keys(adds);
  const count = editKeys.length + addKeys.length + deletes.size;

  $('#pendingCount').textContent = count;
  const host = $('#pendingList');
  if (!count) {
    host.innerHTML = '<div class="pendingItem empty">暂无改动</div>';
  } else {
    const item = (op, key, label) =>
      '<div class="pendingItem" data-key="' + esc(key) + '" data-op="' + op + '">' +
      '<span class="op ' + op + '">' + label + '</span>' +
      '<span class="k">' + esc(disp(key)) + '</span></div>';
    host.innerHTML =
      editKeys.map((k) => item('edit', k, '改')).join('') +
      addKeys.map((k) => item('add', k, '增')).join('') +
      Array.from(deletes).map((k) => item('del', k, '删')).join('');
  }

  const info = $('#bottomInfo');
  if (count) {
    info.className = 'pendInfo dirty';
    info.innerHTML = '待保存 <b>' + count + '</b> 条改动 —— 还没写盘，点「保存改动」才生效';
  } else {
    info.className = 'pendInfo';
    info.textContent = '无待保存改动';
  }
  $('#btnSave').disabled = count === 0;
}

// ---------------------------------------------------------------------------
// 保存 / 预览 / 导出
// ---------------------------------------------------------------------------
function pendingPayload() {
  return {
    edits: S.pending.edits,
    adds: S.pending.adds,
    deletes: Array.from(S.pending.deletes),
  };
}

async function previewDiff() {
  const result = await post('/api/preview', pendingPayload());
  openModal('改动预览（尚未写盘）', diffHtml(result));
}

function diffHtml(result) {
  const notes = (result.notes || []).length
    ? '<div class="card-note">提示：' + result.notes.map(esc).join('<br>') + '</div>' : '';
  const head = '<div class="stat">条目数 ' + result.old_count + ' → ' + result.new_count +
    '　·　diff ' + result.diff.length + ' 行</div>';
  if (!result.diff.length) return notes + head + '<p class="hint">没有实际改动。</p>';
  const body = result.diff.map((line) => {
    let cls = 'ctx';
    if (line.startsWith('+++') || line.startsWith('---')) cls = 'hunk';
    else if (line.startsWith('@@')) cls = 'hunk';
    else if (line.startsWith('+')) cls = 'add';
    else if (line.startsWith('-')) cls = 'del';
    return '<span class="' + cls + '">' + esc(line) + '</span>';
  }).join('');
  return notes + head + '<pre class="diff">' + body + '</pre>';
}

async function save() {
  const count = Object.keys(S.pending.edits).length + Object.keys(S.pending.adds).length +
    S.pending.deletes.size;
  if (!count) return;
  const deploy = $('#chkDeploy').checked;
  if (!confirm('将要写盘：' + count + ' 条改动' +
      (deploy ? '，并把数据同步到游戏运行目录。' : '。') +
      '\n\n写盘前会自动备份到 data/.backups/，写盘后自动复核不变量；' +
      '复核不过会直接回滚。继续？')) return;

  const result = await post('/api/save', Object.assign(pendingPayload(), { deploy: deploy }));
  if (!result.ok) {
    openModal('保存未通过：' + (result.reason || '未知原因'),
      '<div class="warn">' + esc(result.reason || '') + '<br>' +
      (result.problems || []).map(esc).join('<br>') + '</div>' + diffHtml(result));
    toast('保存失败，已回滚', 'bad');
    return;
  }

  S.pending = { edits: {}, adds: {}, deletes: new Set() };
  S.meta = result.meta;
  S.detail = null;
  renderMeta();
  renderPending();
  renderDetail();
  renderGrid();
  await loadEntries();
  await loadAudit();

  let message = '已保存 ' + result.count + ' 条';
  if (result.backup) message += '，备份：' + result.backup.split(/[\\/]/).pop();
  if (result.deploy) {
    message += result.deploy.ok ? '，并已同步到运行目录' : '；同步失败：' + result.deploy.reason;
  }
  toast(message, result.deploy && !result.deploy.ok ? 'bad' : 'ok');
  renderFiles();
  if (result.diff && result.diff.length) openModal('保存成功', diffHtml({
    diff: result.diff, notes: result.notes || [],
    old_count: result.count - Object.keys(result.dropped || []).length -
              Object.keys(result.touched || []).length,
    new_count: result.count,
  }));
}

async function exportPending() {
  const payload = {
    note: '翻译审核台导出的待保存改动（可直接给批量脚本复用）',
    edits: S.pending.edits,
    adds: S.pending.adds,
    deletes: Array.from(S.pending.deletes),
  };
  if (!Object.keys(payload.edits).length && !Object.keys(payload.adds).length &&
      !payload.deletes.length) { toast('没有待保存改动', 'bad'); return; }
  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = 'review-pending.json';
  a.click();
  URL.revokeObjectURL(url);
}

function addEntry() {
  openModal('新增词条', `
    <div class="formRow"><label>键（原文）</label>
      <input type="text" id="newKey" spellcheck="false" placeholder="例：FUEL factory"></div>
    <div class="formRow"><label>译文</label>
      <textarea id="newVal" spellcheck="false" placeholder="例：燃油工厂"></textarea></div>
    <p class="hint">
      键会按码点升序自动插到正确位置，不会重排其它行。<br>
      整句（唯一、无歧义）用<b>裸键</b>；短标签照实机的 <code>[作用域]</code> 登记；
      含换行的文本只能用 <code>~</code> 整段模板。
      先按 <code>Shift+Enter</code> 或点下面的按钮暂存，再统一保存。
    </p>
    <div class="row2" style="max-width:280px">
      <button class="btn primary" id="btnAddConfirm">暂存新词条</button>
    </div>`);
  $('#btnAddConfirm').addEventListener('click', () => {
    const key = $('#newKey').value.trim();
    const value = undisp($('#newVal').value);
    if (!key) { toast('键不能为空', 'bad'); return; }
    if (!value) { toast('译文不能为空', 'bad'); return; }
    const exists = S.rows.some((r) => r.key === key);
    S.pending.adds[key] = value;
    S.pending.deletes.delete(key);
    renderPending();
    closeModal();
    toast('已暂存新增：' + key + (exists ? '（注意：当前页里已有同名键，保存时会被跳过）' : ''), 'ok');
  });
}

// ---------------------------------------------------------------------------
// 运行期清单
// ---------------------------------------------------------------------------
async function loadRuntime() {
  const data = await api('/api/runtime');
  const records = data.records || [];
  const stat = {};
  records.forEach((r) => { stat[r.verdict] = (stat[r.verdict] || 0) + 1; });
  $('#runtimeStat').textContent =
    '共 ' + records.length + ' 条：可疑缺口 ' + (stat.gap || 0) +
    ' · 疑似有通路 ' + (stat.likely || 0) +
    ' · 已有通路 ' + (stat.covered || 0) +
    ' · 本就该保持原样 ' + (stat.intended || 0);

  const LABELS = {
    gap: '可疑缺口', likely: '疑似有通路', covered: '已有通路', intended: '刻意保留',
  };
  const sortRank = { gap: 0, likely: 1, covered: 2, intended: 3 };
  records.sort((a, b) => (sortRank[a.verdict] - sortRank[b.verdict]) ||
    a.item.localeCompare(b.item));
  $('#runtimeBody').innerHTML = records.map((r) =>
    '<tr><td class="mono">' + esc(r.source) + '</td>' +
    '<td><span class="vbadge ' + r.verdict + '">' +
      esc(LABELS[r.verdict] || r.verdict) + '</span></td>' +
    '<td class="mono">' + esc(r.scope) + '</td>' +
    '<td class="mono">' + esc(disp(r.item)) + '</td>' +
    '<td class="hint">' + esc(disp(r.detail)) + '</td></tr>').join('') ||
    '<tr><td colspan="5" class="hint" style="padding:20px">没有运行期清单，' +
    '或插件目录下还没有 missing.json / untranslated.json（游戏里跑一轮就会生成）。</td></tr>';
}

// ---------------------------------------------------------------------------
// 不一致清单
// ---------------------------------------------------------------------------
async function loadInconsistency() {
  const data = await api('/api/groups');
  $('#tabInconCount').textContent = (data.multi || []).length + (data.case || []).length;

  $('#multiList').innerHTML = (data.multi || []).map((group) => {
    const values = Array.from(new Set(group.items.map((i) => i.value)));
    const suspicious = values.length > 1;
    const rows = group.items.map((i) =>
      '<div class="sameVal" data-key="' + esc(i.key) + '">' +
      '<span class="k">' + esc(disp(i.key)) + '</span>' +
      '<span class="v">' + esc(disp(i.value)) + '</span></div>').join('');
    return '<div class="groupCard"><div class="gt">' + esc(disp(group.norm)) + '</div>' +
      rows + (suspicious ? '' : '') + '</div>';
  }).join('') || '<p class="hint">没有。</p>';

  $('#caseList').innerHTML = (data.case || []).map((group) => {
    const values = Array.from(new Set(group.items.map((i) => i.value)));
    const danger = values.length > 1;
    const rows = group.items.map((i) =>
      '<div class="sameVal" data-key="' + esc(i.key) + '">' +
      '<span class="k">' + esc(disp(i.key)) + '</span>' +
      '<span class="v' + (danger ? ' badVal' : '') + '">' + esc(disp(i.value)) + '</span></div>')
      .join('');
    return '<div class="groupCard"><div class="gt">' + esc(disp(group.norm)) +
      (danger ? '　<span class="tag high">译文不同 → 会静默覆盖</span>'
              : '　<span class="tag low">译文相同，无害冗余</span>') +
      '</div>' + rows + '</div>';
  }).join('') || '<p class="hint">没有。</p>';

  wireJumpers();
}

function wireJumpers() {
  $$('.sameVal, .pendingItem').forEach((el) => {
    if (el.dataset.wired) return;
    el.dataset.wired = '1';
    el.addEventListener('click', () => {
      const key = el.dataset.key;
      if (!key) return;
      showView('entries');
      S.filters.q = '';
      $('#q').value = '';
      S.filters.offset = 0;
      selectRow(key);
    });
  });
}

// ---------------------------------------------------------------------------
// 体检报告
// ---------------------------------------------------------------------------
async function loadAudit() {
  const data = await api('/api/audit');
  const total = (data.problems || []).length;
  $('#tabAuditCount').textContent = total || '';
  const section = (title, cls, items) =>
    '<div class="panel" style="margin:10px 0;padding:10px 12px">' +
    '<div class="panelHead" style="margin-bottom:5px"><h2 style="font-size:13px">' + title +
    ' <span class="tag ' + cls + '">' + items.length + '</span></h2></div>' +
    (items.length
      ? '<div class="stat" style="margin:0">' +
        items.slice(0, 200).map(esc).join('<br>') +
        (items.length > 200 ? '<br>… 其余 ' + (items.length - 200) + ' 条省略' : '') + '</div>'
      : '<p class="hint" style="margin:0">无</p>') +
    '</div>';
  $('#auditBox').innerHTML =
    section('错误（会让 check_data.py 判失败）', 'high', data.problems || []) +
    section('警告（值得逐条判断）', 'mid', data.warnings || []) +
    section('提示', 'low', data.notes || []);
}

// ---------------------------------------------------------------------------
// 文件与部署
// ---------------------------------------------------------------------------
function renderFiles() {
  const files = (S.meta && S.meta.files) || [];
  $('#fileBody').innerHTML = files.map((f) => {
    const repoTable = files.find((x) => x.label === '仓库词表');
    const runTable = files.find((x) => x.label === '运行目录词表');
    const mismatch = f.label === '运行目录词表' &&
      repoTable && runTable && repoTable.md5 !== runTable.md5;
    return '<tr><td>' + esc(f.label) + (mismatch ? ' <span class="tag mid">与仓库不一致</span>' : '') +
      '</td>' +
      '<td class="mono">' + (f.size == null ? '—' : (f.size / 1024).toFixed(1) + ' KB') + '</td>' +
      '<td class="mono">' + (f.md5 ? f.md5.slice(0, 16) : '—') + '</td>' +
      '<td class="mono">' + (f.mtime || '—') + '</td></tr>';
  }).join('');
}

async function deploy() {
  const result = await post('/api/deploy');
  if (!result.ok) { $('#deployOut').textContent = '失败：' + result.reason; toast('同步失败', 'bad'); return; }
  $('#deployOut').textContent = result.results.map((r) =>
    (r.changed ? '已更新  ' : '无变化  ') + r.file + '  ' + r.md5.slice(0, 12)).join('\n') +
    '\n\n完成于 ' + result.mtime;
  S.meta = await api('/api/meta');
  renderMeta();
  renderFiles();
  toast('数据已同步到运行目录', 'ok');
}

// ---------------------------------------------------------------------------
// 视图切换 / 弹层 / 帮助
// ---------------------------------------------------------------------------
function showView(name) {
  S.view = name;
  $$('.tab').forEach((t) => t.classList.toggle('active', t.dataset.view === name));
  $$('.view').forEach((v) => v.classList.toggle('active', v.id === 'view-' + name));
  if (name === 'files') renderFiles();
  if (name === 'runtime') loadRuntime();
  if (name === 'audit') loadAudit();
  if (name === 'inconsistency') loadInconsistency();
}

function openModal(title, html) {
  $('#modalTitle').textContent = title;
  $('#modalBody').innerHTML = html;
  $('#modal').classList.add('show');
}
function closeModal() { $('#modal').classList.remove('show'); }

function renderHelp() {
  $('#helpBox').innerHTML = `
    <h3>这个工具做什么</h3>
    <p>把 <code>data/translation.json</code>（约 4850 条）摊成可检索、可筛选、可编辑的界面，
    并把它按风险标出来，让校对不必从头读一遍全表。它<b>只在本机运行</b>（127.0.0.1），
    只读打开、写盘必须显式点保存。</p>

    <h3>审核动线（建议顺序）</h3>
    <ul>
      <li><b>先看「体检报告」</b>：错误项是会让 <code>tools/check_data.py</code> 判失败的硬伤（括号规范、
      占位符数量、大小写重复键覆盖），先清掉。</li>
      <li><b>再看「不一致清单」</b>：同原文多译文里，除目标代号 identity 与作用域语义分歧外，
      都属于该统一而没统一（历史上就抓出过 <code>score</code> 分数/得分、
      <code>throttle</code> 油门/节流阀这类分裂，以及一条只差尾空格的重复键）。</li>
      <li><b>然后按风险标签筛</b>：<span class="tag high">高危</span> 的两类
      （括号、占位符）必须处理；<span class="tag mid">中危</span> 的 4 类逐条判断；
      <span class="tag low">低危</span> 的「含拉丁字母 / 不含中文」是主战场，
      1092 条里多数是刻意保留的型号与缩写，剩下来就是要补的漏译。</li>
      <li><b>最后看「运行期清单」</b>：游戏跑一轮后 <code>missing.json</code> 会给出
      实机证据，比静态猜测可靠得多。</li>
    </ul>

    <h3>编辑短标签时的两个现成抓手</h3>
    <ul>
      <li><b>宽度</b>：详情面板给出「原文宽度 → 译文宽度」（CJK 记 2、其余记 1）。
      固定宽度的槽位被撑破就是这里超了。</li>
      <li><b>同组键</b>：同一作用域下的兄弟条目并排列出。项目约定是
      「短槽位标签按同组兄弟的字数定长」，对着这一列改最省事。</li>
    </ul>

    <h3>写盘的安全边界</h3>
    <ul>
      <li>未编辑的键，那一行<b>逐字节不动</b> —— diff 精确等于你改的那几条，不给 review 添噪。</li>
      <li>保存前自动备份到 <code>data/.backups/translation.json.&lt;时间戳&gt;</code>（留最近 20 份）。</li>
      <li>保存后立刻复核：合法 JSON、键仍按码点升序、无字面重复键、无 BOM、纯 LF、
      行数与条目数一致、未触碰行未变。任一条不成立就<b>自动回滚</b>。</li>
      <li>「保存后自动同步到运行目录」勾选后，保存成功会顺带把数据推到
      <code>BepInEx/plugins/…</code>（只推数据，不碰 DLL）。</li>
    </ul>

    <h3>注意：改了 DLL 就必须重启游戏</h3>
    <p>本工具只改 json 数据，改完在游戏里按 <b>F11 →「重新加载翻译文件」</b>即可生效、不必重启。
    但如果这一轮同时改了 <code>src/</code> 下的 C# 代码，那是 DLL 变更，
    <b>必须重启游戏</b>才生效。</p>

    <h3>命令行</h3>
    <pre class="out">python tools/review/review_server.py                 # 默认 127.0.0.1:8765 并自动开浏览器
python tools/review/review_server.py --port 9000 --no-browser
python tools/review/review_server.py --selftest      # 只跑一致性自检后退出
python tools/review/review_server.py --plugin-dir "D:\\...\\plugins\\NuclearOptionChineseLocalizationPatch"</pre>
    <p class="hint"><code>--selftest</code> 会在临时副本上真跑一次「编辑 + 新增 + 保存」，
    核对最小 diff 与未触碰行，且保证真词表 md5 不变。改完本工具后先跑它。</p>
  `;
}

// ---------------------------------------------------------------------------
// 事件绑定
// ---------------------------------------------------------------------------
function wire() {
  $$('.tab').forEach((tab) => tab.addEventListener('click', () => showView(tab.dataset.view)));

  $('#btnReload').addEventListener('click', async () => {
    if (Object.keys(S.pending.edits).length || Object.keys(S.pending.adds).length ||
        S.pending.deletes.size) {
      if (!confirm('重新载入会丢掉还没保存的改动。继续？')) return;
      S.pending = { edits: {}, adds: {}, deletes: new Set() };
      renderPending();
    }
    const result = await post('/api/reload');
    S.meta = result.meta;
    S.detail = null;
    renderMeta();
    renderDetail();
    await loadEntries();
    await loadAudit();
    await loadInconsistency();
    loadRuntime();
    toast('已重新载入', 'ok');
  });

  $('#btnTheme').addEventListener('click', () => {
    const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.theme = next;
    try { localStorage.setItem('noReviewTheme', next); } catch (e) { /* 忽略 */ }
  });

  let searchTimer = null;
  $('#q').addEventListener('input', (event) => {
    clearTimeout(searchTimer);
    const value = event.target.value;
    searchTimer = setTimeout(() => {
      S.filters.q = value;
      S.filters.offset = 0;
      loadEntries();
    }, 220);
  });

  $('#kindChips').addEventListener('click', (event) => {
    const chip = event.target.closest('.chip');
    if (!chip) return;
    const kind = chip.dataset.kind;
    if (S.filters.kinds.has(kind)) S.filters.kinds.delete(kind);
    else S.filters.kinds.add(kind);
    chip.classList.toggle('on');
    S.filters.offset = 0;
    loadEntries();
  });

  $('#flagChips').addEventListener('click', (event) => {
    const chip = event.target.closest('.chip');
    if (!chip) return;
    const flag = chip.dataset.flag;
    if (S.filters.flags.has(flag)) S.filters.flags.delete(flag);
    else S.filters.flags.add(flag);
    chip.classList.toggle('on');
    S.filters.offset = 0;
    loadEntries();
  });

  $('#btnFlagNone').addEventListener('click', () => {
    S.filters.flags.clear();
    $$('#flagChips .chip').forEach((c) => c.classList.remove('on'));
    S.filters.offset = 0;
    loadEntries();
  });
  $('#btnFlagHigh').addEventListener('click', () => {
    S.filters.flags.clear();
    Object.keys(S.meta.flag_meta || {}).forEach((f) => {
      if (S.meta.flag_meta[f].level === 'high') S.filters.flags.add(f);
    });
    $$('#flagChips .chip').forEach((c) =>
      c.classList.toggle('on', S.filters.flags.has(c.dataset.flag)));
    S.filters.offset = 0;
    loadEntries();
  });

  $('#scopeSel').addEventListener('change', (event) => {
    S.filters.scope = event.target.value;
    S.filters.offset = 0;
    loadEntries();
  });

  $('#sortSel').addEventListener('change', (event) => {
    S.filters.sort = event.target.value;
    S.filters.offset = 0;
    loadEntries();
  });
  $('#limitSel').addEventListener('change', (event) => {
    S.filters.limit = parseInt(event.target.value, 10);
    S.filters.offset = 0;
    loadEntries();
  });

  $('#btnPrev').addEventListener('click', () => {
    S.filters.offset = Math.max(0, S.filters.offset - S.filters.limit);
    loadEntries();
  });
  $('#btnNext').addEventListener('click', () => {
    if (S.filters.offset + S.filters.limit < S.total) {
      S.filters.offset += S.filters.limit;
      loadEntries();
    }
  });

  $('#gridBody').addEventListener('click', (event) => {
    const row = event.target.closest('tr[data-key]');
    if (row) selectRow(row.dataset.key);
  });

  $('#pendingList').addEventListener('click', (event) => {
    const item = event.target.closest('.pendingItem[data-key]');
    if (!item) return;
    showView('entries');
    selectRow(item.dataset.key);
  });

  $('#btnAdd').addEventListener('click', addEntry);
  $('#btnPreviewDiff').addEventListener('click', () =>
    Object.keys(S.pending.edits).length || Object.keys(S.pending.adds).length ||
    S.pending.deletes.size ? previewDiff() : toast('没有待保存改动', 'bad'));
  $('#btnClearPending').addEventListener('click', () => {
    if (!confirm('丢掉全部未保存改动？')) return;
    S.pending = { edits: {}, adds: {}, deletes: new Set() };
    renderPending();
    renderDetail();
    renderGrid();
  });
  $('#btnSave').addEventListener('click', save);
  $('#btnExport').addEventListener('click', exportPending);
  $('#btnDeploy').addEventListener('click', deploy);
  $('#btnRuntimeReload').addEventListener('click', loadRuntime);
  $('#btnAuditReload').addEventListener('click', loadAudit);

  $('#modalClose').addEventListener('click', closeModal);
  $('#modal').addEventListener('click', (event) => {
    if (event.target.id === 'modal') closeModal();
  });

  document.addEventListener('keydown', (event) => {
    const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(event.target.tagName);
    if (event.key === 'Escape') {
      if ($('#modal').classList.contains('show')) closeModal();
      else if (typing) event.target.blur();
      return;
    }
    if (event.ctrlKey && event.key === 'Enter') {
      event.preventDefault();
      if (S.detail) stageDraft();
      return;
    }
    if (event.ctrlKey && event.key.toLowerCase() === 's') {
      event.preventDefault();
      save();
      return;
    }
    if (typing) return;
    if (event.key === '/') {
      event.preventDefault();
      $('#q').focus();
      return;
    }
    if (event.key === 'j' || event.key === 'k') {
      const index = S.rows.findIndex((r) => r.key === S.selected);
      const next = event.key === 'j'
        ? Math.min(S.rows.length - 1, index + 1)
        : Math.max(0, index - 1);
      if (S.rows[next]) {
        selectRow(S.rows[next].key);
        const el = document.querySelector('tr[data-key="' + CSS.escape(S.rows[next].key) + '"]');
        if (el) el.scrollIntoView({ block: 'nearest' });
      }
    }
  });

  renderPending();
}

// 记住主题
try {
  const saved = localStorage.getItem('noReviewTheme');
  if (saved) document.documentElement.dataset.theme = saved;
} catch (e) { /* 忽略 */ }

boot().catch((error) => {
  document.body.innerHTML =
    '<div style="padding:40px;font-family:sans-serif">' +
    '<h2>审核台启动失败</h2><pre>' + esc(error && error.message) + '</pre>' +
    '<p>请确认后端 <code>review_server.py</code> 仍在运行。</p></div>';
});
