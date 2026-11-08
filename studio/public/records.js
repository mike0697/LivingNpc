// The tabs Characters, Items, Rules and System: a list on the left, a form in the middle, problems / uses / JSON on the right.
// A form edits a copy of the file's object: keys the form does not know are kept, Save writes the JSON (2 spaces, LF).
// The checks (lib/records.js) say what Unity would say when it loads the world.

import {
  KINDS, ITEM_TYPES, SEXES, DAY_KEYS, DAY_NAMES, ID_RE, checkRecord, jsonText, textOf, parseTime, clock, dayMask, daysOf,
  whereAt, routineBlocks, effectsToText, effectsFromText,
} from '/lib/records.js';

const $ = (s) => document.querySelector(s);
const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const clone = (v) => (v === undefined ? undefined : JSON.parse(JSON.stringify(v)));
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

/** h('div', { class: 'x', onclick }, child, 'text', [more]) */
function h(tag, props = {}, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'style' && isObj(v)) { for (const [p, x] of Object.entries(v)) if (x !== null && x !== undefined) { if (p.startsWith('--')) el.style.setProperty(p, x); else el.style[p] = x; } }
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'value') el.value = v;
    else if (k === 'checked') el.checked = !!v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat(Infinity)) if (kid !== null && kid !== undefined && kid !== false) el.append(kid instanceof Node ? kid : String(kid));
  return el;
}

// ---------------------------------------------------------------------------------------------- paths in the model

function getAt(obj, path) { return path.reduce((o, k) => (o === undefined || o === null ? undefined : o[k]), obj); }

/** Sets a value at path (creating objects on the way); undefined removes the key. Keeps the order of existing keys. */
function setAt(obj, path, value) {
  let o = obj;
  for (let i = 0; i < path.length - 1; i++) {
    if (!isObj(o[path[i]]) && !Array.isArray(o[path[i]])) o[path[i]] = typeof path[i + 1] === 'number' ? [] : {};
    o = o[path[i]];
  }
  const last = path[path.length - 1];
  if (value === undefined) { if (Array.isArray(o)) o.splice(last, 1); else delete o[last]; }
  else o[last] = value;
}

/** Renames a key of an object keeping its place. */
function renameKey(obj, from, to) {
  const entries = Object.entries(obj).map(([k, v]) => [k === from ? to : k, v]);
  for (const k of Object.keys(obj)) delete obj[k];
  for (const [k, v] of entries) obj[k] = v;
}

// ---------------------------------------------------------------------------------------------- activity colours

const PALETTE = ['#4f7fd9', '#d9774f', '#3fa36b', '#a35bc9', '#c9a227', '#3aa6b0', '#d0548b', '#7d8a3a'];
function colorOf(key) {
  if (!key) return '#9aa1ad';
  let n = 0;
  for (const c of key) n = (n * 31 + c.charCodeAt(0)) >>> 0;
  return PALETTE[n % PALETTE.length];
}

// ==============================================================================================================

export function createRecords({ api, query, status, world, refs, reloadRefs, escapeHtml, worldCount = () => 1 }) {
  const st = { view: 'npc', data: null, cur: null, mode: 'form', jsonError: null, preview: null, usages: null };

  const kindsOf = (view) => (view === 'npc' ? ['npc', 'schedule'] : view === 'item' ? ['item'] : view === 'system' ? ['title'] : ['rules']);
  /** rules.json and title.json: one file each, no id, nothing uses them by name. */
  const isSingle = (kind) => kind === 'rules' || kind === 'title';
  const records = (kind) => (isSingle(kind) ? (st.data?.[kind] ? [st.data[kind]] : []) : st.data?.[kind] || []);
  const schedulesMap = () => Object.fromEntries(records('schedule').filter((r) => isObj(r.json)).map((r) => [r.json.id, r.json]));
  const lang = () => st.data?.rules?.json?.language || 'it';

  const dirty = () => !!st.cur && (st.cur.isNew || (st.mode === 'json' && st.jsonError) || !same(st.cur.model, st.cur.original));

  function leaveOk() { return !dirty() || confirm('There are unsaved changes. Do you want to leave them?'); }

  // ------------------------------------------------------------------------------------------- loading

  async function load(keep = true) {
    st.data = await api(`/api/records?${query({ world: world() })}`);
    if (keep && st.cur && !st.cur.isNew) {
      const again = records(st.cur.kind).find((r) => r.rel === st.cur.rel);
      if (again) { openRecord(st.cur.kind, again, true); return; }
    }
    st.cur = null;
    const first = kindsOf(st.view).flatMap(records)[0];
    if (first) openRecord(kindsOf(st.view).find((k) => records(k).includes(first)), first, true);
    else render();
  }

  function openRecord(kind, rec, force = false) {
    if (!force && st.cur && st.cur.rel === rec.rel && !st.cur.isNew) return;
    if (!force && !leaveOk()) return;
    const editable = isObj(rec.json);
    st.cur = { kind, rel: rec.rel, original: clone(rec.json), model: clone(rec.json), isNew: false, text: rec.text };
    st.mode = editable ? (st.mode === 'json' ? 'json' : 'form') : 'json';
    st.jsonError = rec.json === null ? 'The file is not valid JSON: fix it here' : null;
    st.preview = null;
    st.usages = null;
    st.jsonDraft = st.sleepDraft = undefined;
    st.sleepErrors = [];
    render();
    loadUsages();
  }

  async function loadUsages() {
    const cur = st.cur;
    if (!cur || cur.isNew || isSingle(cur.kind) || !isObj(cur.model)) return;
    try {
      const { usages } = await api(`/api/usages?${query({ world: world(), kind: cur.kind, id: cur.original?.id || '' })}`);
      if (st.cur === cur) { st.usages = usages; renderSide(); }
    } catch { /* not shown */ }
  }

  // ------------------------------------------------------------------------------------------- the whole view

  function render() {
    renderList();
    renderHead();
    renderForm();
    renderSide();
  }

  function renderList() {
    const box = $('#rec-list');
    box.innerHTML = '';
    const head = $('#rec-new-buttons');
    head.innerHTML = '';
    if (st.view === 'npc') {
      head.append(h('button', { type: 'button', onclick: () => newRecord('npc') }, '+ Character'), h('button', { type: 'button', onclick: () => newRecord('schedule') }, '+ Routine'));
    } else if (st.view === 'item') {
      head.append(h('button', { type: 'button', onclick: () => newRecord('item') }, '+ Item'));
    } else if (st.view === 'system') {
      if (!st.data?.title && !(st.cur?.isNew && st.cur.kind === 'title')) head.append(h('button', { type: 'button', onclick: () => newRecord('title') }, '+ Create title.json'));
      else head.append(h('span', { class: 'muted' }, 'Main menu and buttons (title.json)'));
    } else head.append(h('span', { class: 'muted' }, 'The rules of the world (rules.json)'));

    const groups = st.view === 'npc' ? [['npc', 'Characters (NPC)'], ['schedule', 'Shared routines']] : st.view === 'item' ? [['item', 'Items']] : st.view === 'system' ? [['title', 'System']] : [['rules', 'Rules']];
    for (const [kind, title] of groups) {
      box.append(h('div', { class: 'doc-group' }, title));
      const list = [...records(kind)];
      if (st.cur?.isNew && st.cur.kind === kind) list.unshift({ rel: st.cur.rel, json: st.cur.model, isNew: true });
      if (!list.length) box.append(h('div', { class: 'doc muted' }, kind === 'rules' ? 'rules.json is missing' : kind === 'title' ? 'title.json is missing: the game uses the basic menu' : 'none'));
      for (const r of list) {
        const active = st.cur && st.cur.rel === r.rel;
        const json = active ? st.cur.model : r.json;
        const name = isSingle(kind) ? r.rel : isObj(json) ? textOf(json.name, lang()) || json.id || r.rel : '(file to fix)';
        const sub = r.isNew ? 'not saved yet' : kind === 'rules' ? 'start, stats, activities, sleep' : kind === 'title' ? 'main menu, music, buttons' : (isObj(json) ? json.id : r.rel);
        const badge = { npc: ['C', 'p'], schedule: ['R', 'r'], item: ['I', 'o'], rules: ['⚙', 'g'], title: ['★', 'g'] }[kind];
        const mark = active && dirty() ? ' •' : '';
        box.append(h('button', {
          type: 'button', class: `doc${active ? ' active' : ''}${r.isNew ? ' unsaved' : ''}${isObj(json) ? '' : ' broken'}`,
          onclick: () => (r.isNew ? null : openRecord(kind, records(kind).find((x) => x.rel === r.rel))),
        }, h('span', { class: `badge ${badge[1]}` }, badge[0]), name + mark, h('small', {}, sub)));
      }
      if (kind === 'rules' && st.cur?.kind === 'rules' && st.mode === 'form') {
        for (const [id, label] of RULE_SECTIONS) box.append(h('button', { type: 'button', class: 'doc sub', onclick: () => document.getElementById(`sec-${id}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' }) }, label));
      }
    }
  }

  function renderHead() {
    const cur = st.cur;
    $('#rec-title').textContent = !cur ? 'Nothing open' : cur.kind === 'rules' ? 'Rules of the world' : cur.kind === 'title' ? 'System: main menu and buttons' : isObj(cur.model) ? `${KINDS[cur.kind].label}: ${textOf(cur.model.name, lang()) || cur.model.id || ''}` : cur.rel;
    $('#rec-file').textContent = !cur ? '' : cur.isNew ? `new: will be saved to ${cur.rel}` : cur.rel;
    for (const b of document.querySelectorAll('.rec-mode')) {
      b.classList.toggle('active', b.dataset.mode === st.mode);
      b.disabled = !cur || (b.dataset.mode === 'form' && !isObj(cur.model));
    }
    $('#rec-delete').hidden = !cur || isSingle(cur.kind);
    $('#save').classList.toggle('dirty', dirty());
  }

  /** After a change to the model: problems, JSON, list title, save marker. */
  function changed({ list = true } = {}) {
    renderSide();
    renderHead();
    if (list) renderList();
    renderTimeline();
    renderPreview();
    renderTitlePreview();
  }

  // ------------------------------------------------------------------------------------------- side panel

  function problems() {
    const cur = st.cur;
    if (!cur) return { errors: [], warnings: [] };
    if (st.mode === 'json' && st.jsonError) return { errors: [st.jsonError], warnings: [] };
    const taken = cur.isNew ? records(cur.kind).map((r) => r.json?.id) : null;
    const r = checkRecord(cur.kind, cur.model, refs(), taken);
    if (cur.kind === 'rules' && st.sleepErrors?.length) r.errors.push(...st.sleepErrors.map((e) => `sleep effects, ${e}`));
    return r;
  }

  function renderSide() {
    const box = $('#rec-problems');
    box.innerHTML = '';
    const { errors, warnings } = problems();
    if (!st.cur) return;
    if (!errors.length && !warnings.length) box.append(h('div', { class: 'problem ok' }, 'No problems: it can be saved.'));
    for (const e of errors) box.append(h('div', { class: 'problem err' }, h('b', {}, 'Error'), ' ', e));
    for (const w of warnings) box.append(h('div', { class: 'problem warn' }, h('b', {}, 'Warning'), ' ', w));

    const use = $('#rec-usages');
    use.innerHTML = '';
    $('#rec-usages-wrap').hidden = !st.cur || isSingle(st.cur.kind);
    if (st.cur.isNew) use.append(h('div', { class: 'muted' }, 'Nothing yet: it is new.'));
    else if (!st.usages) use.append(h('div', { class: 'muted' }, '…'));
    else if (!st.usages.length) use.append(h('div', { class: 'muted' }, 'No quest, message or character uses it.'));
    else {
      const byFile = new Map();
      for (const u of st.usages) { if (!byFile.has(u.rel)) byFile.set(u.rel, []); byFile.get(u.rel).push(u.where); }
      use.append(h('div', { class: 'muted usage-count' }, `${byFile.size} ${byFile.size === 1 ? 'file' : 'files'}`));
      for (const [rel, list] of byFile) use.append(h('div', { class: 'usage' }, h('code', {}, rel), h('span', {}, list.join(', '))));
    }

    $('#rec-json').textContent = st.mode === 'json' && st.jsonError ? 'Fix the JSON to see it.' : isObj(st.cur.model) || Array.isArray(st.cur.model) ? jsonText(st.cur.model) : '';
  }

  // ------------------------------------------------------------------------------------------- new, save, delete

  function newRecord(kind) {
    if (!leaveOk()) return;
    if (kind === 'title') {          // one file with a fixed name: nothing to ask
      st.cur = { kind, rel: 'title.json', original: null, model: KINDS.title.fresh(), isNew: true };
      st.mode = 'form'; st.jsonError = null; st.preview = null; st.usages = [];
      st.jsonDraft = st.sleepDraft = undefined; st.sleepErrors = [];
      render();
      return;
    }
    const dlg = $('#rec-new');
    $('#rec-new-title').textContent = `New: ${KINDS[kind].label.toLowerCase()}`;
    $('#rec-new-error').textContent = '';
    const idIn = $('#rec-new-id'), nameIn = $('#rec-new-name');
    idIn.value = ''; nameIn.value = '';
    $('#rec-new-name-wrap').hidden = kind === 'schedule';
    dlg.querySelector('form').onsubmit = (e) => {
      if (e.submitter?.value === 'cancel') return;
      e.preventDefault();
      const id = idIn.value.trim();
      if (!ID_RE.test(id)) { $('#rec-new-error').textContent = 'The id can only have letters, digits, _ - . (it becomes the file name)'; return; }
      if (records(kind).some((r) => r.json?.id === id)) { $('#rec-new-error').textContent = `“${id}” already exists`; return; }
      const model = KINDS[kind].fresh(id);
      if (nameIn.value.trim() && 'name' in model) model.name = nameIn.value.trim();
      dlg.close();
      st.cur = { kind, rel: `${KINDS[kind].dir}/${id}.json`, original: null, model, isNew: true };
      st.mode = 'form'; st.jsonError = null; st.preview = null; st.usages = [];
      st.jsonDraft = st.sleepDraft = undefined; st.sleepErrors = [];
      render();
    };
    dlg.showModal();
    idIn.focus();
  }

  async function save() {
    const cur = st.cur;
    if (!cur) return;
    const { errors } = problems();
    if (errors.length) { status('Not saved: there are errors (see “Problems” on the right)', 'bad'); return; }
    if (!dirty()) { status('Nothing to save: the file is already like this', 'ok'); return; }
    try {
      const res = await api(`/api/save?${query({ world: world() })}`, {
        method: 'POST', headers: { 'content-type': 'application/json', 'x-livingnpc-studio': '1' },
        body: JSON.stringify({ write: { [cur.rel]: jsonText(cur.model) }, remove: [] }),
      });
      cur.isNew = false;
      await reloadRefs();
      await load(true);
      status(res.changed.length ? `Saved: ${cur.rel}` : 'Saved: the file was already like this', 'ok');
    } catch (e) { status(`Not saved: ${e.message}`, 'bad'); }
  }

  async function remove() {
    const cur = st.cur;
    if (!cur || isSingle(cur.kind)) return;
    if (cur.isNew) { st.cur = null; await load(false); return; }
    let uses = st.usages;
    try { uses = (await api(`/api/usages?${query({ world: world(), kind: cur.kind, id: cur.original?.id || '' })}`)).usages; } catch { /* use what we have */ }
    const warn = uses?.length ? `\n\nWARNING: it is used in ${uses.length} ${uses.length === 1 ? 'place' : 'places'}, which will cause errors in the game:\n${uses.slice(0, 12).map((u) => `${u.rel}: ${u.where}`).join('\n')}${uses.length > 12 ? '\n…' : ''}` : '';
    if (!confirm(`Delete ${cur.rel}?${warn}`)) return;
    try {
      await api(`/api/save?${query({ world: world() })}`, {
        method: 'POST', headers: { 'content-type': 'application/json', 'x-livingnpc-studio': '1' },
        body: JSON.stringify({ write: {}, remove: [cur.rel] }),
      });
      st.cur = null;
      await reloadRefs();
      await load(false);
      status(`Deleted: ${cur.rel}`, 'ok');
    } catch (e) { status(`Not deleted: ${e.message}`, 'bad'); }
  }

  // ------------------------------------------------------------------------------------------- the form

  const RULE_SECTIONS = [['start', 'Start'], ['inventory', 'Starting inventory'], ['stats', 'Stats'], ['activities', 'Activities'], ['dayparts', 'Dayparts'], ['sleep', 'Sleep'], ['goals', 'Goals'], ['auto', 'Automatic quests']];

  function renderForm() {
    const box = $('#rec-form');
    const scroll = box.scrollTop;
    box.innerHTML = '';
    const cur = st.cur;
    if (!cur) { box.append(h('p', { class: 'muted empty' }, st.view === 'rules' ? 'This world has no rules.json.' : st.view === 'system' ? 'This world has no title.json: the game menu is the basic one. Press “+ Create title.json” to choose its look.' : 'Choose on the left what to open, or create a new one.'));
      if (st.view === 'system' && st.data) box.append(sec(null, 'Game title', null, worldNameRow()));
      return;
    }
    if (st.mode === 'json') { box.append(jsonEditor()); return; }
    const m = cur.model;
    if (cur.kind === 'npc') npcForm(box, m);
    else if (cur.kind === 'schedule') scheduleForm(box, m);
    else if (cur.kind === 'item') itemForm(box, m);
    else if (cur.kind === 'title') systemForm(box, m);
    else rulesForm(box, m);
    box.scrollTop = scroll;
    renderTimeline();
    renderPreview();
    renderTitlePreview();
  }

  function jsonEditor() {
    const cur = st.cur;
    const text = st.jsonError && cur.text !== undefined && cur.model === null ? cur.text : jsonText(cur.model);
    const ta = h('textarea', { class: 'json-edit', spellcheck: 'false' });
    ta.value = st.jsonDraft ?? text;
    ta.addEventListener('input', () => {
      st.jsonDraft = ta.value;
      try {
        const v = JSON.parse(ta.value);
        cur.model = v;
        st.jsonError = null;
      } catch (e) { st.jsonError = `Invalid JSON: ${e.message}`; }
      changed();
    });
    return h('div', { class: 'json-wrap' }, h('p', { class: 'hint' }, 'The file as it is. Here you can also change the fields the form does not show.'), ta);
  }

  // ---- small building blocks

  const sec = (id, title, help, ...kids) => h('section', { class: 'sec', id: id ? `sec-${id}` : null }, h('h3', {}, title), help ? h('p', { class: 'hint' }, help) : null, ...kids);
  const row = (label, input, help) => h('label', { class: 'frow' }, h('span', {}, label), input, help ? h('small', { class: 'muted' }, help) : null);

  function datalist(kind) {
    const id = `rdl-${kind}`;
    let el = document.getElementById(id);
    if (!el) { el = h('datalist', { id }); document.body.append(el); }
    const r = refs() || {};
    let list = r[kind] || [];
    if (kind === 'place') list = [...(r.node || []), ...Object.keys(st.cur?.model?.places || {}).map((p) => `$${p}`)];
    if (kind === 'bg') list = st.data.nodes.map((n) => n.id);
    if (kind === 'relwho') list = [...(r.npc || []).filter((n) => n !== st.cur?.model?.id), 'player'];
    el.innerHTML = list.map((o) => `<option value="${escapeHtml(o)}">`).join('');
    return id;
  }

  /** A text input bound to path; empty removes the key when optional. */
  function text(path, { list, placeholder, optional = true, onDone } = {}) {
    const v = getAt(st.cur.model, path);
    return h('input', {
      type: 'text', value: v ?? '', placeholder, list: list ? datalist(list) : null,
      oninput: (e) => { const s = e.target.value; setAt(st.cur.model, path, s === '' && optional ? undefined : s); changed({ list: path[0] === 'name' }); },
      onchange: onDone,
    });
  }

  function num(path, { placeholder, int = false, optional = true, onInput } = {}) {
    const v = getAt(st.cur.model, path);
    return h('input', {
      type: 'number', step: int ? '1' : 'any', value: v ?? '', placeholder, class: 'num',
      oninput: (e) => {
        const s = e.target.value.trim();
        const n = s === '' ? undefined : Number(s);
        if (n !== undefined && Number.isNaN(n)) return;
        setAt(st.cur.model, path, n === undefined && !optional ? 0 : n);
        onInput?.(n);
        changed({ list: false });
      },
    });
  }

  function time(path, fallback) {
    const v = getAt(st.cur.model, path);
    return h('input', {
      type: 'text', value: v ?? '', placeholder: fallback || 'HH:mm', class: 'time', maxlength: 5,
      oninput: (e) => { setAt(st.cur.model, path, e.target.value.trim() || undefined); changed({ list: false }); },
    });
  }

  function select(path, options, { optional = false } = {}) {
    const v = getAt(st.cur.model, path);
    const el = h('select', { onchange: (e) => { setAt(st.cur.model, path, e.target.value === '' ? undefined : e.target.value); changed(); } },
      optional ? h('option', { value: '' }, '') : null,
      options.map(([val, label]) => h('option', { value: val }, label)));
    const known = options.some(([val]) => val === v);
    if (v !== undefined && !known) el.prepend(h('option', { value: v }, `${v} (unknown)`));
    el.value = v ?? (optional ? '' : options[0]?.[0]);
    return el;
  }

  /** A localized text: "text" or {"it": …, "en": …}. */
  function locText(path, { long = false } = {}) {
    const v = getAt(st.cur.model, path);
    const make = (p, value) => (long
      ? h('textarea', { rows: 2, oninput: (e) => { setAt(st.cur.model, p, e.target.value === '' && p.length === path.length ? undefined : e.target.value); changed({ list: path[0] === 'name' }); } }, value ?? '')
      : h('input', { type: 'text', value: value ?? '', oninput: (e) => { setAt(st.cur.model, p, e.target.value === '' && p.length === path.length ? undefined : e.target.value); changed({ list: path[0] === 'name' }); } }));
    if (isObj(v)) {
      return h('div', { class: 'loc' }, Object.keys(v).map((k) => h('div', { class: 'loc-row' }, h('span', { class: 'lang' }, k), make([...path, k], v[k]),
        h('button', { type: 'button', class: 'mini', title: 'Remove this language', onclick: () => { delete v[k]; if (!Object.keys(v).length) setAt(st.cur.model, path, undefined); renderForm(); changed(); } }, '✕'))),
      h('button', { type: 'button', class: 'mini link', onclick: () => { const k = prompt('Language code (e.g. en, fr)'); if (k && !(k in v)) { v[k] = ''; renderForm(); changed(); } } }, '+ language'));
    }
    return h('div', { class: 'loc' }, make(path, v),
      h('button', { type: 'button', class: 'mini link', title: 'Write the text in more languages', onclick: () => { setAt(st.cur.model, path, { [lang()]: v ?? '', en: '' }); renderForm(); changed(); } }, '+ translations'));
  }

  function tags(path) {
    const v = getAt(st.cur.model, path);
    return h('input', {
      type: 'text', value: Array.isArray(v) ? v.join(', ') : '', placeholder: 'words separated by commas',
      oninput: (e) => { const list = e.target.value.split(',').map((s) => s.trim()).filter(Boolean); setAt(st.cur.model, path, list.length ? list : undefined); changed({ list: false }); },
    });
  }

  /** { key: value } rows: places, stats, inventory, activity effects. */
  function mapEditor(path, { keyLabel, valueLabel, keyList, valueList, valueType = 'number', addLabel = '+ Add', fresh = '' } = {}) {
    const obj = getAt(st.cur.model, path);
    const entries = isObj(obj) ? Object.entries(obj) : [];
    const table = h('div', { class: 'kv' });
    entries.forEach(([k, val]) => {
      const keyIn = h('input', {
        type: 'text', value: k, list: keyList ? datalist(keyList) : null, placeholder: keyLabel,
        onchange: (e) => {
          const nk = e.target.value.trim();
          const o = getAt(st.cur.model, path);
          if (!nk || nk === k) { e.target.value = k; return; }
          if (nk in o) { status(`“${nk}” is already there`, 'bad'); e.target.value = k; return; }
          renameKey(o, k, nk); renderForm(); changed();
        },
      });
      const valIn = valueType === 'number'
        ? h('input', { type: 'number', step: 'any', class: 'num', value: val ?? '', oninput: (e) => { const n = Number(e.target.value); if (e.target.value !== '' && !Number.isNaN(n)) { getAt(st.cur.model, path)[k] = n; changed({ list: false }); } } })
        : h('input', { type: 'text', value: val ?? '', list: valueList ? datalist(valueList) : null, oninput: (e) => { getAt(st.cur.model, path)[k] = e.target.value; changed({ list: false }); } });
      table.append(h('div', { class: 'kv-row' }, keyIn, valIn, h('button', { type: 'button', class: 'mini', title: 'Remove', onclick: () => { const o = getAt(st.cur.model, path); delete o[k]; if (!Object.keys(o).length) setAt(st.cur.model, path, undefined); renderForm(); changed(); } }, '✕')));
    });
    if (entries.length) table.prepend(h('div', { class: 'kv-row head' }, h('span', {}, keyLabel), h('span', {}, valueLabel), h('span')));
    table.append(h('button', {
      type: 'button', class: 'mini link', onclick: () => {
        const o = getAt(st.cur.model, path);
        let k = 'new', n = 1;
        while (isObj(o) && k in o) k = `new${++n}`;
        setAt(st.cur.model, [...path, k], valueType === 'number' ? (fresh === '' ? 1 : fresh) : fresh);
        renderForm(); changed();
      },
    }, addLabel));
    return table;
  }

  /** The 7 day toggles of a block. */
  function daysEditor(path) {
    const v = getAt(st.cur.model, path);
    const mask = dayMask(v);
    if (mask < 0) return h('span', { class: 'bad-days' }, `${JSON.stringify(v)} `, h('button', { type: 'button', class: 'mini', onclick: () => { setAt(st.cur.model, path, 'all'); renderForm(); changed(); } }, 'every day'));
    return h('span', { class: 'days' }, DAY_KEYS.map((d, i) => h('button', {
      type: 'button', class: `day${mask & (1 << i) ? ' on' : ''}`, title: DAY_NAMES[i],
      onclick: () => { const nm = mask ^ (1 << i); setAt(st.cur.model, path, nm ? daysOf(nm) : [DAY_KEYS[i]]); renderForm(); changed(); },
    }, DAY_NAMES[i].slice(0, 2))));
  }

  /** The rows of a list of routine entries (npc.routine or schedule.blocks). */
  function blocksEditor(path, { allowSchedules }) {
    const list = getAt(st.cur.model, path) || [];
    const box = h('div', { class: 'blocks' });
    const head = h('div', { class: 'block head' }, ['Days', 'From', 'To', 'Where', 'Activity', 'Priority', 'Name', ''].map((t) => h('span', {}, t)));
    if (list.length) box.append(head);
    const move = (i, d) => { const a = getAt(st.cur.model, path); const j = i + d; if (j < 0 || j >= a.length) return; [a[i], a[j]] = [a[j], a[i]]; renderForm(); changed(); };
    const tools = (i) => h('span', { class: 'tools' },
      h('button', { type: 'button', class: 'mini', title: 'Up', onclick: () => move(i, -1) }, '↑'),
      h('button', { type: 'button', class: 'mini', title: 'Down', onclick: () => move(i, 1) }, '↓'),
      h('button', { type: 'button', class: 'mini', title: 'Remove', onclick: () => { getAt(st.cur.model, path).splice(i, 1); renderForm(); changed(); } }, '✕'));
    list.forEach((b, i) => {
      if (typeof b === 'string' || (isObj(b) && b.schedule !== undefined)) {
        const sid = typeof b === 'string' ? b : b.schedule;
        const opts = records('schedule').filter((r) => isObj(r.json)).map((r) => [r.json.id, r.json.id]);
        const sel = h('select', { onchange: (e) => { const a = getAt(st.cur.model, path); a[i] = typeof b === 'string' ? e.target.value : { ...b, schedule: e.target.value }; changed(); } }, opts.map(([v, l]) => h('option', { value: v }, l)));
        if (!opts.some(([v]) => v === sid)) sel.prepend(h('option', { value: sid }, `${sid} (does not exist)`));
        sel.value = sid;
        const n = schedulesMap()[sid]?.blocks?.length;
        box.append(h('div', { class: 'block sched' }, h('span', { class: 'sched-label' }, 'Shared routine'), sel, h('span', { class: 'muted' }, n !== undefined ? `${n} ${n === 1 ? 'block' : 'blocks'}` : ''), tools(i)));
        return;
      }
      if (!isObj(b)) { box.append(h('div', { class: 'block' }, h('code', {}, JSON.stringify(b)), tools(i))); return; }
      const p = [...path, i];
      box.append(h('div', { class: 'block' },
        daysEditor([...p, 'days']),
        time([...p, 'from'], '00:00'),
        time([...p, 'to'], '24:00'),
        text([...p, 'at'], { list: 'place', placeholder: 'place or $home' }),
        text([...p, 'activity'], { list: 'activity', placeholder: 'activity' }),
        num([...p, 'priority'], { int: true, placeholder: '0' }),
        text([...p, 'id'], { placeholder: 'optional' }),
        tools(i)));
    });
    box.append(h('div', { class: 'block-add' },
      h('button', { type: 'button', class: 'mini link', onclick: () => { const a = getAt(st.cur.model, path) || []; a.push({ days: 'all', from: '08:00', to: '12:00', at: allowSchedules && st.cur.model.places?.home ? '$home' : '', priority: 10 }); setAt(st.cur.model, path, a); renderForm(); changed(); } }, '+ Block'),
      allowSchedules ? h('button', { type: 'button', class: 'mini link', onclick: () => { const first = records('schedule').find((r) => isObj(r.json))?.json.id; if (!first) { status('There are no shared routines: create one with “+ Routine”', 'bad'); return; } const a = getAt(st.cur.model, path) || []; a.push(first); setAt(st.cur.model, path, a); renderForm(); changed(); } }, '+ Shared routine') : null));
    return box;
  }

  // ---- the week: what the game does with the blocks

  function timelineFor() {
    const cur = st.cur;
    if (!cur || st.mode !== 'form') return null;
    if (cur.kind === 'npc') return { npc: cur.model, schedules: schedulesMap() };
    if (cur.kind === 'schedule' && isObj(cur.model)) return { npc: { places: {}, routine: [cur.model.id || '_'] }, schedules: { ...schedulesMap(), [cur.model.id || '_']: cur.model } };
    return null;
  }

  function renderTimeline() {
    const box = document.getElementById('timeline');
    if (!box) return;
    const t = timelineFor();
    box.innerHTML = '';
    if (!t) return;
    const grid = h('div', { class: 'tl' });
    grid.append(h('div', { class: 'tl-hours' }, h('span'), h('div', { class: 'tl-scale' }, [0, 3, 6, 9, 12, 15, 18, 21, 24].map((hh) => h('span', { style: { left: `${(hh / 24) * 100}%` } }, `${hh}`)))));
    for (let dow = 0; dow < 7; dow++) {
      const lane = h('div', { class: 'tl-lane' });
      let seg = null;
      const flush = (end) => { if (seg) { seg.end = end; lane.append(segmentEl(seg, dow)); } };
      for (let m = 0; m < 1440; m += 5) {
        const w = whereAt(t.npc, t.schedules, dow, m);
        const key = `${w.node}|${w.activity}|${w.block ? w.block.id + w.block.start : '-'}`;
        if (!seg || seg.key !== key) { flush(m); seg = { key, start: m, w }; }
      }
      flush(1440);
      grid.append(h('div', { class: 'tl-row' }, h('span', { class: 'tl-day' }, DAY_NAMES[dow].slice(0, 3)), lane));
    }
    const acts = [...new Set(routineBlocks(t.npc.routine, t.schedules).map((b) => b.activity))];
    box.append(grid, h('div', { class: 'tl-legend' },
      acts.map((a) => h('span', {}, h('i', { style: { background: colorOf(a) } }), a || 'no activity')),
      h('span', {}, h('i', { class: 'home' }), cur().kind === 'npc' ? 'no block: at home' : 'no block')));
    box.append(whereTool(t));
  }
  const cur = () => st.cur;

  function segmentEl(seg, dow) {
    const { w } = seg;
    const label = w.block ? `${w.node}${w.activity ? ' · ' + w.activity : ''}` : (cur().kind === 'npc' && w.node ? `${w.node} (home)` : '');
    const tip = `${DAY_NAMES[dow]} ${clock(seg.start)}-${clock(seg.end)}\n${w.block ? `${w.node || '?'}${w.activity ? `, ${w.activity}` : ''}\n${w.block.from}${w.block.id ? ` “${w.block.id}”` : ''}, priority ${w.block.priority}` : 'no block: stays at home'}`;
    return h('div', {
      class: `tl-seg${w.block ? '' : ' home'}`, title: tip,
      style: { left: `${(seg.start / 1440) * 100}%`, width: `${((seg.end - seg.start) / 1440) * 100}%`, background: w.block ? colorOf(w.activity) : null },
    }, (seg.end - seg.start) >= 150 ? label : '');
  }

  function whereTool(t) {
    const daySel = h('select', {}, DAY_NAMES.map((d, i) => h('option', { value: i }, d)));
    const timeIn = h('input', { type: 'text', class: 'time', value: st.whereTime || '10:00', maxlength: 5 });
    daySel.value = st.whereDay ?? 0;
    const out = h('span', { class: 'where-out' });
    const update = () => {
      st.whereDay = Number(daySel.value); st.whereTime = timeIn.value;
      const m = parseTime(timeIn.value);
      if (m === null || m >= 1440) { out.textContent = 'write a time HH:mm'; return; }
      const w = whereAt(t.npc, t.schedules, Number(daySel.value), m);
      out.textContent = w.block ? `at ${w.node || '?'}${w.activity ? `, doing “${w.activity}”` : ''} (block ${w.block.id ? `“${w.block.id}”` : `from ${clock(w.block.start)}`}, priority ${w.block.priority})` : cur().kind === 'npc' ? `no block: at home${w.node ? ` (${w.node})` : ''}` : 'no block';
    };
    daySel.addEventListener('change', update);
    timeIn.addEventListener('input', update);
    update();
    return h('div', { class: 'where' }, h('b', {}, 'Where is it? '), daySel, ' at ', timeIn, ' → ', out);
  }

  // ---- images

  function imageEditor(path, title, { removable = false } = {}) {
    const img = getAt(st.cur.model, path);
    const isPreview = same(st.preview, path);
    const fileIn = h('input', { type: 'text', value: img?.file ?? '', list: datalist('images'), placeholder: 'folder/file.png', oninput: (e) => { setAt(st.cur.model, [...path, 'file'], e.target.value || undefined); changed({ list: false }); } });
    const upload = h('input', { type: 'file', accept: '.png,.jpg,.jpeg,.webp', hidden: true, onchange: (e) => uploadImage(e.target.files[0], path) });
    const nodesCount = isObj(img?.nodes) ? Object.keys(img.nodes).length : 0;
    return h('div', { class: `img-card${isPreview ? ' previewing' : ''}` },
      h('div', { class: 'img-head' }, h('b', {}, title),
        h('button', { type: 'button', class: 'mini link', onclick: () => { st.preview = path; renderForm(); } }, isPreview ? 'in preview' : 'preview'),
        removable ? h('button', { type: 'button', class: 'mini', title: 'Remove', onclick: () => { setAt(st.cur.model, path, undefined); cleanImages(); if (isPreview) st.preview = null; renderForm(); changed(); } }, '✕') : null),
      h('div', { class: 'img-fields' },
        h('label', { class: 'grow' }, 'File ', fileIn, h('button', { type: 'button', class: 'mini', onclick: () => upload.click() }, 'Upload…'), upload),
        h('label', {}, 'X % ', num([...path, 'x'], { placeholder: '0' })),
        h('label', {}, 'Y % ', num([...path, 'y'], { placeholder: '0' })),
        h('label', {}, 'Width % ', num([...path, 'width'], { placeholder: '30' })),
        h('label', {}, 'Height % ', num([...path, 'height'], { placeholder: 'auto' }))),
      nodesCount ? h('small', { class: 'muted' }, `${nodesCount} ${nodesCount === 1 ? 'variant' : 'variants'} per place (change them from the JSON view)`) : null);
  }

  function cleanImages() {
    const imgs = st.cur.model.images;
    if (!isObj(imgs)) return;
    for (const g of ['activity', 'talk']) if (isObj(imgs[g]) && !Object.keys(imgs[g]).length) delete imgs[g];
    if (!Object.keys(imgs).length) delete st.cur.model.images;
  }

  async function uploadImage(file, path) {
    if (!file) return;
    const folder = prompt('Which folder of images should it go in?', st.cur.kind === 'npc' ? 'people' : '') ?? null;
    if (folder === null) return;
    const name = file.name.replace(/[^A-Za-z0-9_.\- ]/g, '_');
    const rel = `images/${folder.replace(/^\/+|\/+$/g, '') ? folder.replace(/^\/+|\/+$/g, '') + '/' : ''}${name}`;
    const data = await new Promise((ok, ko) => { const r = new FileReader(); r.onload = () => ok(String(r.result).split(',')[1]); r.onerror = ko; r.readAsDataURL(file); });
    try {
      const res = await api(`/api/upload?${query({ world: world() })}`, { method: 'POST', headers: { 'content-type': 'application/json', 'x-livingnpc-studio': '1' }, body: JSON.stringify({ path: rel, data }) });
      await reloadRefs();
      setAt(st.cur.model, [...path, 'file'], res.path);
      st.preview = path;
      status(`Image uploaded: ${rel}`, 'ok');
      renderForm(); changed();
    } catch (e) { status(`Image not uploaded: ${e.message}`, 'bad'); }
  }

  /** The picture in a 16:9 frame over a background of the map, as GameView.PlaceImage puts it; drag it to move it. */
  function renderPreview() {
    const box = document.getElementById('img-preview');
    if (!box) return;
    box.innerHTML = '';
    const path = st.preview;
    const img = path ? getAt(st.cur.model, path) : null;
    const nodes = st.data.nodes;
    const nodeSel = h('select', {}, h('option', { value: '' }, '(no background)'), nodes.map((n) => h('option', { value: n.id }, n.id)));
    const home = st.cur.model.places?.home;
    st.previewNode = st.previewNode ?? (nodes.some((n) => n.id === home) ? home : nodes[0]?.id || '');
    nodeSel.value = st.previewNode;
    const node = nodes.find((n) => n.id === st.previewNode);
    const bgKeys = Object.keys(node?.backgrounds || {});
    const bgSel = h('select', {}, bgKeys.map((k) => h('option', { value: k }, k)));
    if (!bgKeys.includes(st.previewBg)) st.previewBg = bgKeys.includes('day') ? 'day' : bgKeys[0];
    bgSel.value = st.previewBg || '';
    nodeSel.addEventListener('change', () => { st.previewNode = nodeSel.value; renderPreview(); });
    bgSel.addEventListener('change', () => { st.previewBg = bgSel.value; renderPreview(); });
    const frame = h('div', { class: 'frame' });
    const bg = node?.backgrounds?.[st.previewBg];
    if (bg) frame.style.backgroundImage = `url("/api/image?${query({ world: world(), path: bg })}")`;
    if (img?.file) {
      const pic = h('img', { src: `/api/image?${query({ world: world(), path: `images/${img.file}` })}`, draggable: 'false', alt: '' });
      const place = () => {
        pic.style.left = `${img.x ?? 0}%`; pic.style.top = `${img.y ?? 0}%`; pic.style.width = `${img.width ?? 30}%`;
        pic.style.height = img.height !== undefined ? `${img.height}%` : 'auto';
      };
      place();
      pic.addEventListener('error', () => { frame.append(h('div', { class: 'frame-msg' }, `Cannot find images/${img.file}`)); });
      pic.addEventListener('pointerdown', (e) => {
        e.preventDefault();
        pic.setPointerCapture(e.pointerId);
        const r = frame.getBoundingClientRect();
        const sx = e.clientX, sy = e.clientY, x0 = img.x ?? 0, y0 = img.y ?? 0;
        const moveTo = (ev) => {
          img.x = Math.round((x0 + ((ev.clientX - sx) / r.width) * 100) * 10) / 10;
          img.y = Math.round((y0 + ((ev.clientY - sy) / r.height) * 100) * 10) / 10;
          place();
        };
        pic.onpointermove = moveTo;
        pic.onpointerup = (ev) => { moveTo(ev); pic.onpointermove = pic.onpointerup = null; renderForm(); changed({ list: false }); };
      });
      frame.append(pic);
    } else frame.append(h('div', { class: 'frame-msg' }, path ? 'Choose a file to see the image' : 'Press “preview” on an image'));
    box.append(h('div', { class: 'preview-bar' }, 'Background: ', nodeSel, ' ', bgSel, h('small', { class: 'muted' }, ' drag the image to move it')), frame);
  }

  // ---- the forms

  function idRow(m) {
    return row('Id', st.cur.isNew ? h('input', { type: 'text', value: m.id, disabled: true }) : h('input', { type: 'text', value: m.id ?? '', disabled: true }),
      st.cur.isNew ? 'chosen at creation: it becomes the file name' : 'changing it means updating the quests and messages that use it');
  }

  function npcForm(box, m) {
    box.append(
      sec(null, 'Who they are', null, idRow(m),
        row('First name', locText(['firstName'])), row('Last name', locText(['lastName'])),
        row('Name', locText(['name']), 'what the game shows; empty = first name and last name'),
        row('Age', num(['age'], { int: true })), row('Sex', select(['sex'], SEXES, { optional: true }), 'the texts can say {x:him|her|them}'),
        row('Description', locText(['description'], { long: true })), row('Tags', tags(['tags']))),
      sec(null, 'Automatic quests', 'A character with automatic quests on picks by itself, when it is free, one of the automatic quests (templates) it can do, weighted by its goal, stats and relations.',
        row('Automatic quests', check(['autoQuests'], 'picks and does them by itself', false)),
        row('Goal', text(['goal'], { list: 'goal', placeholder: 'none' }), 'one of the goals of the Rules tab'),
        h('h4', {}, 'Relations'),
        h('p', { class: 'hint' }, 'What they think of the others at the start, from -100 (hate) to 100 (love); not written = 0.'),
        mapEditor(['relations'], { keyLabel: 'Towards', valueLabel: 'Value', keyList: 'relwho', addLabel: '+ Relation', fresh: 10 })),
      sec(null, 'Their places', 'The character\'s own place names (home, work…) that the routine uses as $home, $work. “home” is where they go back when no block applies. A setPlace in the quests can change them.',
        mapEditor(['places'], { keyLabel: 'Name', valueLabel: 'Map place', valueType: 'text', valueList: 'node', addLabel: '+ Place', fresh: '' })),
      sec(null, 'Routine', 'Where they are and what they do during the week. If several blocks apply at once the highest priority wins (on a tie, the higher one in the list). A block that goes past midnight (21:00-07:00) continues the next day.',
        blocksEditor(['routine'], { allowSchedules: true }), h('div', { id: 'timeline' })),
      sec(null, 'Stats and inventory', 'The character\'s starting values. Stats not written here start from the starting value in rules.json.',
        h('div', { class: 'two' },
          h('div', {}, h('h4', {}, 'Stats'), mapEditor(['stats'], { keyLabel: 'Stat', valueLabel: 'Value', keyList: 'stat', addLabel: '+ Stat', fresh: 0 })),
          h('div', {}, h('h4', {}, 'Inventory'), mapEditor(['inventory'], { keyLabel: 'Item', valueLabel: 'Amount', keyList: 'item', addLabel: '+ Item', fresh: 1 })))),
      imagesSection(m));
  }

  function imagesSection(m) {
    const imgs = isObj(m.images) ? m.images : {};
    const add = (group) => {
      const name = prompt(group === 'activity' ? 'For which activity? (e.g. sleep, work)' : 'Pose name (default, happy, sad…)', group === 'talk' && !imgs.talk?.default ? 'default' : '');
      if (!name) return;
      setAt(st.cur.model, ['images', group, name], { file: '', x: 20, y: 10, width: 30 });
      delete getAt(st.cur.model, ['images', group, name]).file;
      st.preview = ['images', group, name];
      renderForm(); changed();
    };
    return sec(null, 'Images', 'Positions are in % of the place screen (16:9), from the top-left corner; an empty height follows the image proportions. On the map: the activity image if there is one, otherwise the normal one. When talking: the pose of the dialogue line, otherwise “default”.',
      h('div', { class: 'imgs' },
        h('div', { class: 'img-list' },
          imgs.default ? imageEditor(['images', 'default'], 'On the map', { removable: true }) : h('button', { type: 'button', class: 'mini link', onclick: () => { setAt(st.cur.model, ['images', 'default'], { x: 20, y: 10, width: 30 }); st.preview = ['images', 'default']; renderForm(); changed(); } }, '+ Image on the map'),
          Object.keys(isObj(imgs.activity) ? imgs.activity : {}).map((k) => imageEditor(['images', 'activity', k], `While doing “${k}”`, { removable: true })),
          h('button', { type: 'button', class: 'mini link', onclick: () => add('activity') }, '+ Image for an activity'),
          Object.keys(isObj(imgs.talk) ? imgs.talk : {}).map((k) => imageEditor(['images', 'talk', k], `When talking, pose “${k}”`, { removable: true })),
          h('button', { type: 'button', class: 'mini link', onclick: () => add('talk') }, '+ Image when talking')),
        h('div', { id: 'img-preview', class: 'img-preview' })));
  }

  function scheduleForm(box, m) {
    box.append(
      sec(null, 'Shared routine', 'Blocks several characters can use: in a character\'s routine choose “+ Shared routine”. Here “$home” and the like stand for each character\'s own place.',
        idRow(m), blocksEditor(['blocks'], { allowSchedules: false }), h('div', { id: 'timeline' })));
  }

  function itemForm(box, m) {
    box.append(sec(null, 'Item', null,
      idRow(m),
      row('Name', locText(['name'])),
      row('Description', locText(['description'], { long: true })),
      row('Type', select(['type'], ITEM_TYPES, { optional: true }), 'the game reads it but does not use it yet: it is a label'),
      row('Value', num(['value'], { int: true, placeholder: '0' }), 'the price, for when there are shops'),
      row('Tags', tags(['tags']))),
    h('p', { class: 'hint note' }, 'Icon, use from the inventory (effects) and equipment bonuses: the game does not support them yet, so they do not appear here.'));
  }

  function rulesForm(box, r) {
    const startNode = st.data.startNode;
    const startMinute = r.startMinute ?? 480;
    box.append(
      sec('start', 'Start', 'How a new game begins.',
        row('Start time', h('input', {
          type: 'text', class: 'time', maxlength: 5, value: clock(startMinute),
          oninput: (e) => { const m = parseTime(e.target.value); if (m !== null && m < 1440) { r.startMinute = m; changed({ list: false }); } },
        }), `in the file: startMinute = ${startMinute}`),
        row('Start day', (() => {
          const s = h('select', { onchange: (e) => { r.startDayOfWeek = Number(e.target.value); changed({ list: false }); } }, DAY_NAMES.map((d, i) => h('option', { value: i }, d)));
          s.value = (((r.startDayOfWeek ?? 0) % 7) + 7) % 7;
          return s;
        })()),
        row('Start place', h('input', { type: 'text', value: startNode || '(not set)', disabled: true }), 'changed in the map editor (project.json)'),
        row('Minutes per step', num(['tickMinutes'], { int: true, placeholder: '5' }), 'how far the clock moves at every game step'),
        row('Minutes of a link', num(['defaultLinkMinutes'], { int: true, placeholder: '10' }), 'how long a move takes when the link does not say otherwise'),
        row('Language', text(['language'], { placeholder: 'it' }))),
      sec('inventory', 'Player\'s starting inventory', null,
        mapEditor(['playerInventory'], { keyLabel: 'Item', valueLabel: 'Amount', keyList: 'item', addLabel: '+ Item', fresh: 1 })),
      sec('stats', 'Stats', 'Values of the player and of the characters. Every hour a value moves back towards its starting value by “drift per hour” (a fraction) and changes by “constant per hour”.', statsTable(r)),
      sec('activities', 'Activities', 'What a character does in a routine block, with its effect per hour on the stats.', activitiesTable(r)),
      sec('dayparts', 'Dayparts', 'Which background of the places shows at what time (the background name is the one from the map editor: day, night…).', dayPartsTable(r)),
      sec('sleep', 'Sleep', 'When the player can sleep, what time they wake up and what happens when they sleep.', sleepEditor(r)),
      sec('goals', 'Goals', 'What a character can want (a character has one goal, quests can change it with setGoal). The automatic quests weigh them.', goalsTable(r)),
      sec('auto', 'Automatic quests', 'How often the free characters look for an automatic quest, how many can run at once, and how long a character rests after one.',
        row('Check every', num(['autoQuests', 'checkMinutes'], { int: true, placeholder: '60' }), 'minutes of game time'),
        row('At most at once', num(['autoQuests', 'maxActive'], { int: true, placeholder: '3' }), 'in the whole world; 0 = none'),
        row('Pause after one', num(['autoQuests', 'pauseDays'], { placeholder: '1' }), 'days; a template can say its own (pauseDays)')));
  }

  function listTable(path, cols, fresh, addLabel) {
    const list = getAt(st.cur.model, path);
    const box = h('div', { class: 'ltable', style: { '--cols': cols.map((c) => c.w || '1fr').join(' ') + ' auto' } });
    box.append(h('div', { class: 'lrow head' }, cols.map((c) => h('span', { title: c.tip || '' }, c.label)), h('span')));
    (Array.isArray(list) ? list : []).forEach((item, i) => {
      if (!isObj(item)) { box.append(h('div', { class: 'lrow' }, h('code', {}, JSON.stringify(item)))); return; }
      box.append(h('div', { class: 'lrow' }, cols.map((c) => c.cell([...path, i], item)),
        h('button', { type: 'button', class: 'mini', title: 'Remove', onclick: () => { getAt(st.cur.model, path).splice(i, 1); renderForm(); changed(); } }, '✕')));
    });
    box.append(h('button', { type: 'button', class: 'mini link', onclick: () => { const a = getAt(st.cur.model, path) || []; a.push(fresh()); setAt(st.cur.model, path, a); renderForm(); changed(); } }, addLabel));
    return box;
  }

  function goalsTable() {
    return listTable(['goals'], [
      { label: 'Id', w: '180px', cell: (p) => text([...p, 'id'], { optional: false }) },
      { label: 'Name', w: '1fr', cell: (p) => locText([...p, 'name']) },
    ], () => ({ id: 'new', name: 'New' }), '+ Goal');
  }

  function statsTable() {
    return listTable(['stats'], [
      { label: 'Id', cell: (p) => text([...p, 'id'], { optional: false }) },
      { label: 'Name', w: '1.4fr', cell: (p) => locText([...p, 'name']) },
      { label: 'Min', w: '70px', cell: (p) => num([...p, 'min'], { placeholder: '0' }) },
      { label: 'Max', w: '70px', cell: (p) => num([...p, 'max'], { placeholder: '100' }) },
      { label: 'Start', w: '70px', cell: (p) => num([...p, 'baseline'], { placeholder: '50' }) },
      { label: 'Drift/hour', w: '84px', tip: 'driftPerHour: fraction per hour towards the starting value', cell: (p) => num([...p, 'driftPerHour'], { placeholder: '0' }) },
      { label: 'Constant/hour', w: '84px', tip: 'constantPerHour: fixed change per hour', cell: (p) => num([...p, 'constantPerHour'], { placeholder: '0' }) },
    ], () => ({ id: 'new', name: 'New', min: 0, max: 100, baseline: 50, driftPerHour: 0 }), '+ Stat');
  }

  function activitiesTable() {
    return listTable(['activities'], [
      { label: 'Id', w: '140px', cell: (p) => text([...p, 'id'], { optional: false }) },
      { label: 'Name', w: '180px', cell: (p) => locText([...p, 'name']) },
      { label: 'Effects per hour', w: '1fr', cell: (p) => mapEditor([...p, 'effects'], { keyLabel: 'Stat', valueLabel: 'Per hour', keyList: 'stat', addLabel: '+ Effect', fresh: 1 }) },
    ], () => ({ id: 'new', name: 'New', effects: {} }), '+ Activity');
  }

  function dayPartsTable() {
    const keys = [...new Set(st.data.nodes.flatMap((n) => Object.keys(n.backgrounds)))];
    let dl = document.getElementById('rdl-bgkeys');
    if (!dl) { dl = h('datalist', { id: 'rdl-bgkeys' }); document.body.append(dl); }
    dl.innerHTML = keys.map((k) => `<option value="${escapeHtml(k)}">`).join('');
    return listTable(['dayParts'], [
      { label: 'Id', cell: (p) => text([...p, 'id'], { optional: false }) },
      { label: 'From', w: '80px', cell: (p) => time([...p, 'from'], '00:00') },
      { label: 'To', w: '80px', cell: (p) => time([...p, 'to'], '24:00') },
      { label: 'Background', cell: (p) => { const el = text([...p, 'background'], { placeholder: 'day' }); el.setAttribute('list', 'rdl-bgkeys'); return el; } },
    ], () => ({ id: 'new', from: '00:00', to: '24:00', background: 'day' }), '+ Daypart');
  }

  function sleepEditor(r) {
    const s = isObj(r.sleep) ? r.sleep : null;
    if (!s) return h('button', { type: 'button', class: 'mini link', onclick: () => { r.sleep = { from: '20:00', wakeAt: '07:00', effects: [] }; renderForm(); changed(); } }, '+ Add sleep');
    const ta = h('textarea', { class: 'code-lines', rows: 4, spellcheck: 'false' });
    ta.value = st.sleepDraft ?? effectsToText(s.effects);
    ta.addEventListener('input', () => {
      st.sleepDraft = ta.value;
      const res = effectsFromText(ta.value, refs());
      st.sleepErrors = res.errors;
      if (!res.errors.length) { if (res.effects.length || s.effects !== undefined) s.effects = res.effects; }
      changed({ list: false });
    });
    return h('div', {},
      row('Can sleep from', time(['sleep', 'from'], '20:00')),
      row('Wakes up at', time(['sleep', 'wakeAt'], '07:00')),
      row('Effects when sleeping', ta, 'one command per line, as in the quests: setStat(energy, 100)'));
  }

  // ------------------------------------------------------------------------------------------- System (title.json)

  const imageUrl = (rel) => `/api/image?${query({ world: world(), path: rel })}`;

  /** A select of the files of a folder (refs.images / refs.music) the game can read, "(none)" removes the key. */
  function fileSelect(path, kind, noneLabel = '(none)') {
    const v = getAt(st.cur.model, path);
    const ok = kind === 'music' ? /\.(mp3|ogg|wav)$/i : /\.(png|jpe?g)$/i;
    const list = (refs()?.[kind] || []).filter((f) => ok.test(f));
    const el = h('select', { class: 'file-select', onchange: (e) => { setAt(st.cur.model, path, e.target.value || undefined); renderForm(); changed({ list: false }); } },
      h('option', { value: '' }, noneLabel), list.map((f) => h('option', { value: f }, f)));
    if (v && !list.includes(v)) el.prepend(h('option', { value: v }, `${v} (not found)`));
    el.value = v ?? '';
    return el;
  }

  /** A checkbox for a true / false key whose missing value means <paramref name="fallback"/>. */
  function check(path, label, fallback = true) {
    const v = getAt(st.cur.model, path);
    return h('label', { class: 'check' }, h('input', {
      type: 'checkbox', checked: v === undefined ? fallback : v === true,
      onchange: (e) => { setAt(st.cur.model, path, e.target.checked); renderForm(); changed({ list: false }); },
    }), ' ', label);
  }

  /** Uploads a file into images/<folder> or music/<folder> and puts its name (as the files write it) at path. */
  async function uploadTo(file, root, defaultFolder, path) {
    if (!file) return;
    const folder = prompt(`Which folder of ${root} should it go in? (empty = ${root} itself)`, defaultFolder);
    if (folder === null) return;
    const clean = folder.replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
    const rel = `${root}/${clean ? clean + '/' : ''}${file.name.replace(/[^A-Za-z0-9_.\- ]/g, '_')}`;
    const data = await new Promise((ok, ko) => { const r = new FileReader(); r.onload = () => ok(String(r.result).split(',')[1]); r.onerror = ko; r.readAsDataURL(file); });
    try {
      const res = await api(`/api/upload?${query({ world: world() })}`, { method: 'POST', headers: { 'content-type': 'application/json', 'x-livingnpc-studio': '1' }, body: JSON.stringify({ path: rel, data }) });
      await reloadRefs();
      setAt(st.cur.model, path, res.path);
      status(`Uploaded: ${rel}`, 'ok');
      renderForm(); changed({ list: false });
    } catch (e) { status(`Not uploaded: ${e.message}`, 'bad'); }
  }

  function uploadButton(root, defaultFolder, path, accept) {
    const input = h('input', { type: 'file', accept, hidden: true, onchange: (e) => uploadTo(e.target.files[0], root, defaultFolder, path) });
    return h('span', {}, h('button', { type: 'button', class: 'mini', onclick: () => input.click() }, 'Upload…'), input);
  }

  /** The colour of the words: a colour picker, the code written by hand, and "white" to go back to the default. */
  function colorEditor(path) {
    const v = getAt(st.cur.model, path);
    const hex = typeof v === 'string' && /^#[0-9a-f]{6}$/i.test(v.trim()) ? v.trim() : '#ffffff';
    const set = (value) => { setAt(st.cur.model, path, value); changed({ list: false }); };
    const text = h('input', { type: 'text', class: 'color-code', value: v ?? '', placeholder: '#ffffff (white)', oninput: (e) => { set(e.target.value.trim() || undefined); } });
    const pick = h('input', { type: 'color', value: hex, oninput: (e) => { text.value = e.target.value; set(e.target.value); } });
    return h('span', { class: 'color-edit' }, pick, text, h('button', { type: 'button', class: 'mini', onclick: () => { setAt(st.cur.model, path, undefined); renderForm(); changed({ list: false }); } }, 'white'));
  }

  /** The editor of a set of button pictures: normal, hover / pressed, ends that keep their shape, colour of the words. */
  function buttonsEditor(key) {
    const b = getAt(st.cur.model, [key]);
    if (!isObj(b)) return null;
    return h('div', { class: 'btn-edit' },
      row('Normal image', h('span', { class: 'file-row' }, fileSelect([key, 'normal'], 'images'), uploadButton('images', 'Buttons', [key, 'normal'], '.png,.jpg,.jpeg,.webp'))),
      row('Hover / pressed', h('span', { class: 'file-row' }, fileSelect([key, 'hover'], 'images', '(the same)'), uploadButton('images', 'Buttons', [key, 'hover'], '.png,.jpg,.jpeg,.webp')),
        'when the mouse is over it, while it is pressed and when it is selected'),
      row('Ends (pixels)', num([key, 'border'], { int: true, placeholder: '0' }),
        'how many pixels on the left and right of the image keep their shape; the rest stretches sideways. For a round button: half the height of the image'),
      row('Text colour', colorEditor([key, 'textColor'])));
  }

  /**
   * The title of the game: "name" of project.json, saved at once with its own button (it is not part of title.json).
   * The folder of the world keeps its name: the game keeps the saves by folder, so renaming it would hide them.
   */
  function worldNameRow() {
    const saved = st.data.worldName || '';
    const input = h('input', { type: 'text', value: saved, maxlength: 80, placeholder: `(no title: the game shows “${world()}”, the folder name)` });
    const btn = h('button', { type: 'button', class: 'mini', disabled: true }, 'Save title');
    const sync = () => { btn.disabled = !input.value.trim() || input.value.trim() === saved; };
    const commit = async () => {
      if (btn.disabled) return;
      try {
        const res = await api(`/api/world-name?${query({ world: world() })}`, { method: 'POST', headers: { 'content-type': 'application/json', 'x-livingnpc-studio': '1' }, body: JSON.stringify({ name: input.value }) });
        st.data.worldName = res.name;
        status(`Game title: ${res.name}`, 'ok');
        renderForm();
      } catch (e) { status(`Title not saved: ${e.message}`, 'bad'); }
    };
    input.addEventListener('input', sync);
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); commit(); } });
    btn.addEventListener('click', commit);
    return row('Game title', h('span', { class: 'file-row' }, input, btn),
      `the name in the game menu, saved right away in project.json. The world folder stays “${world()}”: it does not need the same name`);
  }

  function systemForm(box, m) {
    const sameMenu = !isObj(m.menuButtons);
    box.append(
      sec(null, 'Title screen', 'What you see when the game starts (title.json in the pack).',
        worldNameRow(),
        row('Draw the title', check(['drawTitle'], 'write the title over the image'), 'untick it if the title is already drawn in the image'),
        row('Background image', h('span', { class: 'file-row' }, fileSelect(['image'], 'images', '(none: dark background)'), uploadButton('images', 'title', ['image'], '.png,.jpg,.jpeg,.webp')),
          'fills the screen, cropped at the edges if its proportions are not 16:9'),
        h('div', { id: 'title-preview', class: 'title-preview' })),
      sec(null, 'Title music', 'Loops while you stay in the menu, and fades out when the game starts.',
        row('Music', check(['musicOn'], 'music on')),
        row('File', h('span', { class: 'file-row' }, fileSelect(['music'], 'music', '(none)'), uploadButton('music', '', ['music'], '.mp3,.ogg,.wav'))),
        m.music ? row('Listen', h('audio', { controls: true, preload: 'none', src: `/api/audio?${query({ world: world(), path: `music/${m.music}` })}` })) : null,
        row('Volume', h('span', { class: 'volume' },
          h('input', { type: 'range', min: 0, max: 1, step: 0.05, value: m.volume ?? 1, oninput: (e) => { m.volume = Number(e.target.value); e.target.nextSibling.textContent = ` ${Math.round(m.volume * 100)}%`; changed({ list: false }); } }),
          h('span', {}, ` ${Math.round((m.volume ?? 1) * 100)}%`)))),
      sec(null, 'Game buttons', 'The images of every button inside the game (moves, Wait, Journal, answers…). Without images the plain coloured buttons stay.',
        isObj(m.buttons) ? buttonsEditor('buttons') : h('button', { type: 'button', class: 'mini link', onclick: () => { m.buttons = { normal: '', hover: '', border: 0 }; delete m.buttons.normal; delete m.buttons.hover; renderForm(); changed({ list: false }); } }, '+ Use images for the buttons'),
        isObj(m.buttons) ? h('button', { type: 'button', class: 'mini link danger-link', onclick: () => { delete m.buttons; renderForm(); changed({ list: false }); } }, 'Back to the plain buttons') : null,
        h('div', { id: 'buttons-preview', class: 'buttons-preview' })),
      sec(null, 'Main menu buttons', 'Continue, Load, New game, Load world, Settings, Quit. They can have different images from the game buttons.',
        h('label', { class: 'check' }, h('input', {
          type: 'checkbox', checked: sameMenu,
          onchange: (e) => { if (e.target.checked) delete m.menuButtons; else m.menuButtons = clone(isObj(m.buttons) ? m.buttons : {}) || {}; renderForm(); changed({ list: false }); },
        }), ' same as the game buttons'),
        sameMenu ? null : buttonsEditor('menuButtons')));
  }

  // ---- previews: the menu at 1920x1080 scaled into the frame (GameBootstrap.DrawMenu), buttons as 3-slice (SkinnedButton)

  const naturalHeights = new Map();
  /** Height of a picture, read once; calls back when it is known. */
  function pictureHeight(rel, then) {
    if (naturalHeights.has(rel)) return naturalHeights.get(rel);
    naturalHeights.set(rel, 0);
    const img = new Image();
    img.onload = () => { naturalHeights.set(rel, img.naturalHeight); then(); };
    img.src = imageUrl(rel);
    return 0;
  }

  /**
   * Dresses a preview button as the game draws it: the picture scaled to the button's height as a whole, the ends
   * (border) kept, the middle stretched sideways; the hover picture under the mouse. Sizes are in 1920-wide units (cqw).
   */
  function dressButton(el, skin, heightUnits) {
    const cq = (v) => `${(v / 1920) * 100}cqw`;
    el.style.height = cq(heightUnits);
    if (!isObj(skin) || !skin.normal) return false;
    const normal = `images/${skin.normal}`, hover = `images/${skin.hover || skin.normal}`;
    el.classList.add('skinned');
    el.style.setProperty('--n', `url("${imageUrl(normal)}")`);
    el.style.setProperty('--h', `url("${imageUrl(hover)}")`);
    el.style.color = skin.textColor || '#ffffff';
    const border = Number.isInteger(skin.border) && skin.border > 0 ? skin.border : 0;
    const ih = pictureHeight(normal, () => renderTitlePreview());
    if (border && ih) {
      el.style.borderImageSlice = `0 ${border} fill`;
      el.style.borderWidth = `0 ${cq((border * heightUnits) / ih)}`;
      el.style.borderImageWidth = `0 ${cq((border * heightUnits) / ih)}`;
    } else el.classList.add('stretch');
    return true;
  }

  function renderTitlePreview() {
    const box = document.getElementById('title-preview');
    const strip = document.getElementById('buttons-preview');
    if (!st.cur || st.cur.kind !== 'title' || !isObj(st.cur.model)) return;
    const m = st.cur.model;
    const cq = (v) => `${(v / 1920) * 100}cqw`;
    if (box) {
      box.innerHTML = '';
      const frame = h('div', { class: 'tframe' });
      if (m.image) frame.style.backgroundImage = `url("${imageUrl(`images/${m.image}`)}")`;
      if (m.drawTitle !== false) frame.append(h('div', { class: 'ttitle', style: { top: cq(140), height: cq(90), fontSize: cq(64) } }, st.data.worldName || world()));
      const skin = isObj(m.menuButtons) ? m.menuButtons : m.buttons;
      const labels = ['Continue', 'Load', 'New game', ...(worldCount() > 1 ? ['Load world'] : []), 'Settings', 'Quit'];
      labels.forEach((label, i) => {
        const b = h('div', { class: 'tbtn', style: { left: cq((1920 - 560) / 2), top: cq(300 + i * 78), width: cq(560), fontSize: cq(28) } }, label);
        dressButton(b, skin, 64);
        frame.append(b);
      });
      box.append(frame, h('small', { class: 'muted' }, 'Preview of the menu as the game draws it (hover over the buttons). The music does not play here: use “Listen”.'));
    }
    if (strip) {
      strip.innerHTML = '';
      const area = h('div', { class: 'tstrip' });
      const bgNode = st.data.nodes.find((n) => n.id === st.data.startNode) || st.data.nodes[0];
      const bg = bgNode?.backgrounds?.day || Object.values(bgNode?.backgrounds || {})[0];
      if (bg) area.style.backgroundImage = `url("${imageUrl(bg)}")`;
      for (const [label, w, hh] of [['Wait 30m', 300, 42], ['Journal', 540, 44], ['Home', 250, 50], ['A longer answer in the dialogue', 760, 56]]) {
        const b = h('div', { class: 'gbtn', style: { width: cq(w), fontSize: cq(Math.min(26, hh * 0.55)) } }, label);
        if (!dressButton(b, m.buttons, hh)) b.classList.add('plain');
        area.append(b);
      }
      strip.append(area, h('small', { class: 'muted' }, 'Some game buttons at game size, over the background of the start place.'));
    }
  }

  // ------------------------------------------------------------------------------------------- public

  return {
    async show(view) {
      if (st.view !== view) { st.view = view; st.cur = null; st.sleepDraft = st.jsonDraft = undefined; st.sleepErrors = []; }
      if (!st.data || !st.cur) await load(false); else render();
    },
    async reload() { st.cur = null; st.data = null; st.sleepDraft = st.jsonDraft = undefined; st.sleepErrors = []; },
    setMode(mode) {
      if (!st.cur || mode === st.mode) return;
      if (mode === 'form' && (st.jsonError || !isObj(st.cur.model))) { status('Fix the JSON first', 'bad'); return; }
      st.mode = mode; st.jsonDraft = undefined;
      render();
    },
    save, remove, dirty, leaveOk,
  };
}
