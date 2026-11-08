// LivingNPC Studio: the documents of a world on the left, the script in the middle, the command buttons on the right.
// Every change is converted to JSON at once (lib/docs.js): problems show with their line, Save writes the files.

import { CodeEditor, indentOf } from './editor.js';
import { EFFECTS, STEPS, CONDITIONS, OPS } from '/lib/specs.js';
import { docScript, docFiles } from '/lib/docs.js';
import { stepLines, condText } from '/lib/script.js';
import { createRecords } from './records.js';

const $ = (s) => document.querySelector(s);
const IN = '    ';
const state = { world: null, docs: [], refs: null, doc: null, original: '', result: null, view: 'quests' };

const store = {
  get: (k) => { try { return localStorage.getItem(k); } catch { return null; } },
  set: (k, v) => { try { localStorage.setItem(k, v); } catch { /* not kept */ } },
};

async function api(path, opts) {
  const r = await fetch(path, opts);
  const data = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(data.error || r.statusText);
  return data;
}
const query = (o) => new URLSearchParams(o).toString();

function status(text, kind = '') {
  const el = $('#status');
  el.textContent = text;
  el.className = `status ${kind}`;
}

const dirty = () => !!state.doc && editor.value !== state.original;

const editor = new CodeEditor($('#editor'), { onChange: () => schedule() });     // Ctrl+S: the page listener below

// ====================================================================== documents

async function loadWorlds() {
  const { worlds } = await api('/api/worlds');
  const sel = $('#world');
  sel.innerHTML = worlds.map((w) => `<option>${w}</option>`).join('');
  const last = store.get('studio.world');
  if (!worlds.length) { status('No world found in the worlds folder', 'bad'); return; }
  sel.value = worlds.includes(last) ? last : worlds[0];
  await loadWorld(sel.value);
}

async function loadWorld(world) {
  state.world = world;
  store.set('studio.world', world);
  [{ docs: state.docs }, state.refs] = await Promise.all([api(`/api/docs?${query({ world })}`), api(`/api/refs?${query({ world })}`)]);
  state.doc = null;
  const last = store.get(`studio.doc.${world}`);
  const first = state.docs.find((d) => d.id === last) || state.docs[0];
  if (first) await openDoc(first.id, true);
  else { editor.value = ''; state.original = ''; renderDocs(); updateTitle(); check(); }
}

function renderDocs() {
  const list = $('#doc-list');
  list.innerHTML = '';
  const docs = [...state.docs];
  if (state.doc?.unsaved) docs.unshift(state.doc);
  const auto = (d) => d.kind === 'template' || d.kind === 'lines';
  for (const [title, test] of [['Quests', (d) => d.kind === 'quest'], ['Automatic quests', auto], ['Messages', (d) => d.kind !== 'quest' && !auto(d)]]) {
    const group = docs.filter(test);
    const head = document.createElement('div');
    head.className = 'doc-group';
    head.textContent = title;
    list.append(head);
    if (!group.length) { const none = document.createElement('div'); none.className = 'doc muted'; none.textContent = 'none'; list.append(none); }
    for (const d of group) {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = `doc${state.doc?.id === d.id ? ' active' : ''}${d.unsaved ? ' unsaved' : ''}`;
      const changed = state.doc?.id === d.id && dirty() ? ' •' : '';
      const [badge, cls] = { quest: ['Q', ''], template: ['A', 'a'], lines: ['L', 'a'] }[d.kind] || ['M', 'm'];
      b.innerHTML = `<span class="badge ${cls}">${badge}</span>${escapeHtml(d.title)}${changed}<small>${escapeHtml(d.unsaved ? 'not saved yet' : d.name + (d.files.length > 1 ? ` · ${d.files.length} files` : ''))}</small>`;
      b.addEventListener('click', () => openDoc(d.id));
      list.append(b);
    }
  }
}

const escapeHtml = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function updateTitle() {
  $('#doc-title').textContent = state.doc ? state.doc.title : 'No document';
  const files = state.doc?.files || [];
  $('#doc-files').textContent = !state.doc ? '' : state.doc.unsaved ? 'new, not saved yet' : files.length === 1 ? files[0] : state.doc.kind === 'lines' ? `${files.length} files in lines/` : `${files.length} files: ${files[0]} and the messages in ${state.doc.eventDir}/`;
  $('#doc-files').title = files.join('\n');
  $('#delete-doc').hidden = !state.doc;
  $('#save').classList.toggle('dirty', dirty());
}

function leaveOk() { return !dirty() || confirm('This document has unsaved changes. Do you want to leave them?'); }

async function openDoc(id, force = false) {
  if (!force && state.doc?.id === id) return;
  if (!force && !leaveOk()) return;
  try {
    const { doc, files } = await api(`/api/doc?${query({ world: state.world, id })}`);
    state.doc = doc;
    editor.value = docScript(files);
    state.original = editor.value;
    store.set(`studio.doc.${state.world}`, id);
    status('');
  } catch (e) { status(`Cannot open it: ${e.message}`, 'bad'); }
  renderDocs(); updateTitle(); check();
}

function startNew(kind) {
  if (!leaveOk()) return;
  const quest = kind === 'quest', auto = kind === 'template';
  openForm({
    title: quest ? 'New quest' : auto ? 'New automatic quest' : 'New message',
    help: quest ? 'A quest with one stage and the message that offers it. You can change everything in the code.'
      : auto ? 'A quest the characters with “Automatic quests” on pick and do by themselves. You can change everything in the code.'
      : 'A message on its own (outside the quests). You can change everything in the code.',
    fields: [{ name: 'id', type: 'text', label: 'Id (letters, digits and _, it becomes the file name)', req: true }],
    ok: ({ id }) => {
      if (!/^[A-Za-z0-9_.\-]+$/.test(id)) return 'The id can only have letters, digits, _ - .';
      const taken = quest ? state.refs.quest.includes(id) : auto ? (state.refs.template || []).includes(id) : state.refs.event.includes(id);
      if (taken) return `“${id}” already exists`;
      state.doc = { id: `new:${id}`, kind: quest ? 'quest' : auto ? 'template' : 'event', title: id, name: id, files: [], eventDir: quest ? `events/${id}` : 'events', unsaved: true };
      editor.value = (quest ? questTemplate(id) : auto ? autoTemplate(id) : eventTemplate(id, 'enterNode')).join('\n') + '\n';
      state.original = '';
      renderDocs(); updateTitle(); check();
      editor.focusLine(1);
      return null;
    },
  });
}

const firstOf = (kind, fallback) => state.refs?.[kind]?.[0] || fallback;

function questTemplate(id) {
  const npc = firstOf('npc', 'character');
  return [
    `quest ${id}:`,
    `${IN}title = "New quest"`,
    `${IN}summary = "What the quest is about"`,
    `${IN}giver = ${npc}`,
    '',
    `${IN}stage start:`,
    `${IN}${IN}text = "What the player has to do"`,
    '',
    ...eventTemplate(`ev_${id}_offer`, 'talk', [`quest(${id}, notStarted)`], [
      `${npc}: "I have a job for you. Will you help me?"`,
      'choice:',
      `${IN}option "Yes, I will help":`,
      `${IN}${IN}startQuest(${id})`,
      `${IN}option "Not now":`,
      `${IN}${IN}pass`,
    ]),
  ];
}

function autoTemplate(id) {
  const item = firstOf('item', 'bread');
  return [
    `template ${id}:`,
    `${IN}title = "{self} visits {friend}"`,
    `${IN}pauseDays = 1`,
    `${IN}weight:`,
    `${IN}${IN}base = 10`,
    `${IN}slot friend:`,
    `${IN}${IN}type = npc`,
    `${IN}${IN}free = true`,
    `${IN}slot house:`,
    `${IN}${IN}type = place`,
    `${IN}${IN}of = friend`,
    `${IN}${IN}place = home`,
    `${IN}texts:`,
    `${IN}${IN}intro:`,
    `${IN}${IN}${IN}"I am going to see {friend}."`,
    `${IN}${IN}talk:`,
    `${IN}${IN}${IN}"Sorry, I am on my way to {friend}."`,
    '',
    `${IN}stage go:`,
    `${IN}${IN}text = "{self} goes to {friend}'s home"`,
    `${IN}${IN}onStart:`,
    `${IN}${IN}${IN}tell(intro)`,
    `${IN}${IN}${IN}moveNpc(self, house, forMinutes=180)`,
    `${IN}${IN}complete:`,
    `${IN}${IN}${IN}npcAt(self, house)`,
    `${IN}${IN}fail:`,
    `${IN}${IN}${IN}elapsed(hours=4)`,
    '',
    `${IN}stage visit:`,
    `${IN}${IN}complete:`,
    `${IN}${IN}${IN}elapsed(minutes=30)`,
    `${IN}${IN}onComplete:`,
    `${IN}${IN}${IN}modRelation(friend, self, 5)`,
    `${IN}${IN}${IN}# giveItem(${item}, target=friend)`,
  ];
}

function eventTemplate(id, trigger, when = ['always()'], body = ['say("Text of the message")']) {
  const head = trigger === 'talk' ? [`trigger = talk`, `npc = ${firstOf('npc', 'character')}`] : [`trigger = ${trigger}`, `node = ${firstOf('node', 'place')}`];
  return [`event ${id}:`, ...head.map((l) => IN + l), `${IN}when:`, ...when.map((l) => IN + IN + l), '', ...body.map((l) => IN + l)];
}

// ====================================================================== checking and saving

let timer = 0;
function schedule() {
  clearTimeout(timer);
  timer = setTimeout(check, 200);
  $('#save').classList.toggle('dirty', dirty());
}

function check() {
  if (!state.doc) { $('#problems').innerHTML = ''; $('#json').textContent = ''; $('#problem-count').textContent = ''; editor.setMarks(new Map()); return; }
  const r = docFiles(editor.value, state.doc, state.refs);
  state.result = r;
  const marks = new Map();
  for (const w of r.warnings) marks.set(w.line, 'warn');
  for (const e of r.errors) marks.set(e.line, 'err');
  editor.setMarks(marks);

  const all = [...r.errors.map((e) => ({ ...e, kind: 'err' })), ...r.warnings.map((w) => ({ ...w, kind: 'warn' }))].sort((a, b) => a.line - b.line);
  const box = $('#problems');
  box.innerHTML = '';
  if (!all.length) box.innerHTML = '<span class="problem ok">No problems: the code can be saved.</span>';
  for (const p of all) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = `problem ${p.kind}`;
    b.innerHTML = `<b>Line ${p.line}</b> ${p.kind === 'err' ? 'Error' : 'Warning'}: ${escapeHtml(p.message)}`;
    b.addEventListener('click', () => editor.focusLine(p.line));
    box.append(b);
  }
  const count = (n, one, many) => (n ? `${n} ${n === 1 ? one : many}` : '');
  const counts = [count(r.errors.length, 'error', 'errors'), count(r.warnings.length, 'warning', 'warnings')].filter(Boolean);
  $('#problem-count').textContent = counts.length ? `(${counts.join(', ')})` : '';

  $('#json').textContent = r.errors.length
    ? 'Fix the errors to see the JSON.'
    : [...Object.entries(r.write).map(([rel, text]) => `── ${rel} ──\n${text}`), ...r.remove.map((rel) => `── ${rel}: will be deleted ──`)].join('\n') || '(nothing)';
  updateTitle();
  if (dirty() !== state.listedDirty) { state.listedDirty = dirty(); renderDocs(); }
}

async function save() {
  if (!state.doc) return;
  check();
  const r = state.result;
  if (r.errors.length) { status('Not saved: there are errors, see “Problems”', 'bad'); showTab('problems'); return; }
  if (!Object.keys(r.write).length && !r.remove.length) { status('The document is empty: nothing to save', 'bad'); return; }
  if (r.remove.length && !confirm(`These files are no longer needed and will be deleted:\n\n${r.remove.join('\n')}\n\nContinue?`)) return;
  try {
    const res = await api(`/api/save?${query({ world: state.world })}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-livingnpc-studio': '1' },
      body: JSON.stringify({ write: r.write, remove: r.remove }),
    });
    state.docs = res.docs;
    state.refs = await api(`/api/refs?${query({ world: state.world })}`);
    const written = Object.keys(r.write);
    state.doc = res.docs.find((d) => d.files.some((f) => written.includes(f))) || state.doc;
    store.set(`studio.doc.${state.world}`, state.doc.id);
    state.original = editor.value;
    status(res.changed.length ? `Saved: ${res.changed.length} ${res.changed.length === 1 ? 'file' : 'files'} written` : 'Saved: the files were already like this', 'ok');
  } catch (e) { status(`Not saved: ${e.message}`, 'bad'); }
  renderDocs(); check();
}

/** Deletes the open document: a quest with all its messages, a folder of messages, or one message. */
async function deleteDoc() {
  const doc = state.doc;
  if (!doc) return;
  if (doc.unsaved) {
    if (!confirm(`“${doc.title}” is not saved yet: discard it?`)) return;
    state.doc = null; state.original = editor.value;
    await loadWorld(state.world);
    return;
  }
  const what = doc.kind === 'quest' ? `the quest “${doc.title}” and its messages` : doc.kind === 'template' ? `the automatic quest “${doc.name}”` : doc.kind === 'lines' ? 'all the shared lines' : doc.kind === 'folder' ? `the messages of the folder “${doc.name}”` : `the message “${doc.name}”`;
  // other quests and messages that still name the quest (startQuest, quest(...) conditions...)
  let warn = '';
  if (doc.kind === 'quest' || doc.kind === 'template') {
    try {
      const { usages } = await api(`/api/usages?${query({ world: state.world, kind: doc.kind, id: doc.name })}`);
      const outside = usages.filter((u) => !doc.files.includes(u.rel));
      if (outside.length) warn = `\n\nWARNING: other files still use it and will cause errors in the game:\n${outside.slice(0, 12).map((u) => `${u.rel}: ${u.where}`).join('\n')}${outside.length > 12 ? '\n…' : ''}`;
    } catch { /* deleted without the list */ }
  }
  const list = doc.files.slice(0, 15).join('\n') + (doc.files.length > 15 ? `\n… and ${doc.files.length - 15} more` : '');
  if (!confirm(`Delete ${what}?\n\n${doc.files.length} ${doc.files.length === 1 ? 'file' : 'files'} will be deleted:\n${list}${dirty() ? '\n\nThe unsaved changes will be lost.' : ''}${warn}`)) return;
  try {
    const res = await api(`/api/save?${query({ world: state.world })}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-livingnpc-studio': '1' },
      body: JSON.stringify({ write: {}, remove: doc.files }),
    });
    state.doc = null; state.original = editor.value;
    await loadWorld(state.world);
    status(`Deleted: ${res.changed.length} ${res.changed.length === 1 ? 'file' : 'files'}`, 'ok');
  } catch (e) { status(`Not deleted: ${e.message}`, 'bad'); }
}

function showTab(name) {
  for (const t of document.querySelectorAll('.ptab')) t.classList.toggle('active', t.dataset.tab === name);
  $('#problems').hidden = name !== 'problems';
  $('#json').hidden = name !== 'json';
}

// ====================================================================== where to put a command

const opens = (l) => l.replace(/#.*$/, '').trimEnd().endsWith(':');
const COND_BLOCKS = /^(when|autoStart|complete|fail|any|all|not)\s*:$/;
const word = (l) => l.trim().replace(/#.*$/, '').trim();

/** Index after the last line of the block that starts at line i. */
function blockEnd(lines, i) {
  const ind = indentOf(lines[i]);
  let last = i;
  for (let j = i + 1; j < lines.length; j++) {
    if (!lines[j].trim()) continue;
    if (indentOf(lines[j]) <= ind) break;
    last = j;
  }
  return last + 1;
}

/** The header of the block a line at indentation ind placed before line start would belong to (-1: top level). */
function parentOf(lines, start, ind) {
  for (let k = start - 1; k >= 0; k--) if (lines[k].trim() && indentOf(lines[k]) < ind) return k;
  return -1;
}

/** Line index of the quest / event / template / lines block around the cursor, or -1. */
function unitAt(lines, i) {
  for (let k = Math.min(i, lines.length - 1); k >= 0; k--) if (lines[k].trim() && indentOf(lines[k]) === 0) return /^(quest|event|template|lines)\b/.test(lines[k]) ? k : -1;
  return -1;
}

/** Where a new line goes for the cursor: { start, count (lines replaced: an empty cursor line is used), ind }. */
function insertionPoint() {
  const lines = editor.lines();
  const i = Math.min(editor.cursorLine(), lines.length - 1);
  const cur = lines[i] ?? '';
  if (cur.trim() === 'pass') return { lines, start: i, count: 1, ind: indentOf(cur) };     // the placeholder makes room for the command
  if (!cur.trim()) {
    let j = i - 1;
    while (j >= 0 && !lines[j].trim()) j--;
    const prev = j >= 0 ? lines[j] : '';
    let ind = j >= 0 ? indentOf(prev) + (opens(prev) ? 4 : 0) : 0;
    if (cur.length) ind = indentOf(cur);
    return { lines, start: i, count: 1, ind };
  }
  return { lines, start: i + 1, count: 0, ind: indentOf(cur) + (opens(cur) ? 4 : 0) };
}

const pad = (ind, rel) => rel.map((l) => (l === '' ? '' : ' '.repeat(ind) + l));

/** A command (one or more lines, the first at indentation 0): at the cursor, but never inside a block of conditions. */
function insertCommand(rel) {
  let { lines, start, count, ind } = insertionPoint();
  if (ind === 0) { status('Put the cursor inside a message (event) or a block of commands', 'bad'); return; }
  for (let p = parentOf(lines, start, ind); p >= 0 && COND_BLOCKS.test(word(lines[p])); p = parentOf(lines, start, ind)) {
    start = blockEnd(lines, p); count = 0; ind = indentOf(lines[p]);
  }
  editor.replaceLines(start, count, pad(ind, rel));
}

/**
 * A condition (its lines, the first at indentation 0): inside the condition block the cursor is in; otherwise in the
 * when: of the event (or complete: of the stage, autoStart: of the quest), created if missing.
 */
function insertCondition(rel) {
  const { lines, start, count, ind } = insertionPoint();
  const parent = parentOf(lines, start, ind);
  if (parent >= 0 && COND_BLOCKS.test(word(lines[parent]))) { editor.replaceLines(start, count, pad(ind, rel)); return; }
  const unit = unitAt(lines, Math.max(0, start - 1));
  if (unit < 0) { status('Put the cursor inside a message (event) or a quest', 'bad'); return; }
  if (/^lines\b/.test(lines[unit])) { status('Put the cursor inside a “variant if …:” line or a block of conditions', 'bad'); return; }
  let owner = unit, blockName = /^(event|template)\b/.test(lines[unit]) ? 'when' : 'autoStart';
  if (!/^event\b/.test(lines[unit])) {
    for (let k = Math.max(0, start - 1); k > unit; k--) if (/^\s*stage\b.*:\s*$/.test(lines[k]) && indentOf(lines[k]) === 4) { owner = k; blockName = 'complete'; break; }
  }
  addToBlock(lines, owner, blockName, rel);
}

/** Appends lines to the named block inside the block that starts at line owner, creating it after the owner's properties. */
function addToBlock(lines, owner, name, rel) {
  const end = blockEnd(lines, owner);
  const childInd = indentOf(lines[owner]) + 4;
  for (let k = owner + 1; k < end; k++) {
    if (indentOf(lines[k]) === childInd && word(lines[k]) === `${name}:`) {
      editor.replaceLines(blockEnd(lines, k), 0, pad(childInd + 4, rel));
      return;
    }
  }
  let at = owner + 1;
  while (at < end && (!lines[at].trim() || (indentOf(lines[at]) === childInd && /^[A-Za-z_]\w*\s*=/.test(lines[at].trim())))) at++;
  while (at > owner + 1 && !lines[at - 1].trim()) at--;
  editor.replaceLines(at, 0, [...pad(childInd, [`${name}:`]), ...pad(childInd + 4, rel)]);
}

/** Sets properties (name = value lines right under the header) of the event around the cursor. */
function setEventProps(props, remove = []) {
  const lines = editor.lines();
  const unit = unitAt(lines, editor.cursorLine());
  if (unit < 0 || !/^event\b/.test(lines[unit])) { status('Put the cursor inside a message (event)', 'bad'); return; }
  const end = blockEnd(lines, unit);
  const all = new Map();
  let k = unit + 1;
  for (; k < end; k++) {
    const t = lines[k].trim();
    if (indentOf(lines[k]) !== 4 || !/^[A-Za-z_]\w*\s*=/.test(t)) break;
    const name = t.split('=')[0].trim();
    if (!remove.includes(name)) all.set(name, lines[k]);
  }
  for (const [n, v] of Object.entries(props)) all.set(n, `${IN}${n} = ${v}`);
  // how the event starts comes first, then the other properties as they were
  const first = ['trigger', 'npc', 'node'].filter((n) => all.has(n));
  const out = [...first, ...[...all.keys()].filter((n) => !first.includes(n))].map((n) => all.get(n));
  const last = Math.max(...Object.keys(props).map((n) => out.indexOf(all.get(n))));
  editor.replaceLines(unit + 1, k - unit - 1, out, last);
}

// ====================================================================== forms

const datalistId = (type) => `dl-${type.replace(/[^a-z]/gi, '-')}`;

function optionsFor(type, values = {}) {
  const r = state.refs || {};
  if (type === 'id:target') return ['player', ...(r.npc || [])];
  if (type === 'id:npc') return [...(r.npc || []), 'player'];
  if (type === 'id:stage') return values.quest ? r.stage?.[values.quest] || [] : Object.values(r.stage || {}).flat();
  if (type === 'id:tstage') return values.template ? r.tstage?.[values.template] || [] : Object.values(r.tstage || {}).flat();
  if (type === 'id:goal') return [...(r.goal || []), 'none'];
  if (type.startsWith('id:')) return r[type.slice(3)] || [];
  if (type.startsWith('file:')) return r[type.slice(5)] || [];
  return [];
}

/**
 * A small form. fields: [{ name, type, label, req }]; ok(values) returns null when done or an error message to show.
 * Empty fields are left out of values; numbers and JSON are converted.
 */
function openForm({ title, help = '', fields, ok, okLabel = 'Insert' }) {
  const dlg = $('#form');
  $('#form-title').textContent = title;
  $('#form-help').textContent = help;
  $('#form-error').textContent = '';
  $('#form-ok').textContent = okLabel;
  const box = $('#form-fields');
  box.innerHTML = '';
  const inputs = {};
  for (const f of fields) {
    const label = document.createElement('label');
    label.innerHTML = `<span class="${f.req ? 'req' : ''}">${escapeHtml(f.label)}</span>`;
    let input;
    if (f.type.startsWith('choice:') || f.type === 'op' || f.type === 'select') {
      input = document.createElement('select');
      const opts = f.type === 'op' ? OPS : f.type === 'select' ? f.options : f.type.slice(7).split('|');
      input.innerHTML = (f.req ? '' : '<option value=""></option>') + opts.map((o) => (Array.isArray(o) ? `<option value="${o[0]}">${escapeHtml(o[1])}</option>` : `<option>${o}</option>`)).join('');
    } else if (f.type === 'text' || f.type === 'json') {
      input = document.createElement(f.type === 'json' || f.long ? 'textarea' : 'input');
      if (f.type === 'json') input.placeholder = '{ … }';
    } else {
      input = document.createElement('input');
      if (f.type === 'number') { input.type = 'number'; input.step = 'any'; }
      if (f.type === 'time') input.placeholder = 'HH:mm';
      if (f.type.startsWith('id:') || f.type.startsWith('file:')) {
        const list = document.createElement('datalist');
        list.id = datalistId(f.name + f.type);
        list.innerHTML = optionsFor(f.type).map((o) => `<option value="${escapeHtml(o)}">`).join('');
        input.setAttribute('list', list.id);
        label.append(list);
      }
    }
    if (f.value !== undefined) input.value = f.value;
    input.name = f.name;
    label.append(input);
    box.append(label);
    inputs[f.name] = { input, f };
  }
  // the stages offered follow the quest (or the automatic quest) chosen in the same form
  for (const [owner, type] of [['quest', 'id:stage'], ['template', 'id:tstage']]) {
    if (!inputs[owner] || !inputs.stage) continue;
    inputs[owner].input.addEventListener('input', () => {
      const list = document.getElementById(inputs.stage.input.getAttribute('list'));
      if (list) list.innerHTML = optionsFor(type, { [owner]: inputs[owner].input.value }).map((o) => `<option value="${escapeHtml(o)}">`).join('');
    });
  }
  // closing a modal dialog gives the focus back to the code with the cursor at the start: it is put back where it was
  const sel = editor.selection();
  dlg.onclose = () => editor.restoreSelection(sel);
  // the form is submitted by the OK button and by Enter in a field; Cancel closes it as it is
  dlg.querySelector('form').onsubmit = (e) => {
    if (e.submitter?.value === 'cancel') return;
    e.preventDefault();
    const values = {};
    for (const { input, f } of Object.values(inputs)) {
      const raw = input.value.trim();
      if (!raw) { if (f.req) { $('#form-error').textContent = `Fill in “${f.label}”`; input.focus(); return; } continue; }
      if (f.type === 'number') { const n = Number(raw); if (Number.isNaN(n)) { $('#form-error').textContent = `“${f.label}” must be a number`; return; } values[f.name] = n; }
      else if (f.type === 'json') { try { values[f.name] = JSON.parse(raw); } catch { $('#form-error').textContent = `“${f.label}” is not valid JSON`; return; } }
      else values[f.name] = f.type === 'text' ? input.value : raw;
    }
    // closed before ok runs: while a modal dialog is open the code cannot take the focus, and the new lines would go into the form
    dlg.onclose = null;              // the close event comes later, when ok has already moved the cursor after the new lines
    dlg.close();
    editor.restoreSelection(sel);
    const problem = ok(values);
    if (problem) { dlg.showModal(); $('#form-error').textContent = problem; }
  };
  dlg.showModal();
  box.querySelector('input, textarea, select')?.focus();
}

const specFields = (spec) => spec.params.map((p) => ({ name: p.name, type: p.type, label: p.label, req: !!p.req, long: p.type === 'text' && /say|text/.test(p.name) }));

/** The script lines of one command object (an effect, a step...). */
function linesOf(step) { const out = []; stepLines([step], '', out); return out; }

function commandForm(name, spec, kind) {
  openForm({
    title: spec.label,
    help: kind === 'step' ? 'Used in messages (event).' : '',
    fields: specFields(spec),
    ok: (v) => { insertCommand(linesOf(kind === 'effect' ? { do: name, ...v } : v)); return null; },
  });
}

function conditionForm(name, spec, done) {
  if (!spec.params.length) { done({ type: name }); return; }
  openForm({ title: `Condition: ${spec.label}`, fields: specFields(spec), ok: (v) => { done({ type: name, ...v }); return null; } });
}

/** First the kind of condition, then its fields. */
function pickCondition(title, done) {
  openForm({
    title,
    fields: [{ name: 'kind', type: 'select', label: 'Which condition', req: true, options: Object.entries(CONDITIONS).map(([n, s]) => [n, s.label]) }],
    okLabel: 'Next',
    ok: ({ kind }) => { setTimeout(() => conditionForm(kind, CONDITIONS[kind], done)); return null; },
  });
}

// ====================================================================== the buttons

function nearestAbove(regex) {
  const lines = editor.lines();
  const i = Math.min(editor.cursorLine(), lines.length - 1);
  let limit = lines[i]?.trim() ? indentOf(lines[i]) : Infinity;
  for (let k = i; k >= 0; k--) {
    if (!lines[k].trim()) continue;
    if (regex.test(lines[k]) && indentOf(lines[k]) <= limit) return { lines, k };
    if (indentOf(lines[k]) === 0) break;
    limit = Math.min(limit, indentOf(lines[k]));
  }
  return null;
}

const TRIGGERS = [
  ['enterNode', 'When entering a place', 'node'],
  ['talk', 'When talking to a character', 'npc'],
  ['tick', 'While time passes (in a place)', 'node'],
  ['wake', 'When waking up', 'node'],
];

function categories() {
  const cats = new Map();
  const add = (cat, item) => { if (!cats.has(cat)) cats.set(cat, []); cats.get(cat).push(item); };

  add('Structure', { label: 'New message (event)', code: 'event …:', run: () => openForm({
    title: 'New message in this document',
    fields: [{ name: 'id', type: 'text', label: 'Id', req: true }, { name: 'trigger', type: 'select', label: 'When it starts', req: true, options: TRIGGERS.map(([t, l]) => [t, l]) }],
    ok: ({ id, trigger }) => {
      if (!/^[A-Za-z0-9_.\-]+$/.test(id)) return 'The id can only have letters, digits, _ - .';
      const lines = editor.lines();
      let end = lines.length;
      while (end > 0 && !lines[end - 1].trim()) end--;
      editor.replaceLines(end, lines.length - end, ['', ...eventTemplate(id, trigger), '']);
      return null;
    },
  }) });
  add('Structure', { label: 'New quest stage (stage)', code: 'stage …:', run: () => {
    const lines = editor.lines();
    const unit = unitAt(lines, editor.cursorLine());
    if (unit < 0 || !/^(quest|template)\b/.test(lines[unit])) { status('Put the cursor inside a quest or an automatic quest', 'bad'); return; }
    openForm({
      title: 'New stage', fields: [{ name: 'id', type: 'text', label: 'Stage id', req: true }, { name: 'text', type: 'text', label: 'Journal text', req: true, long: true }],
      ok: ({ id, text }) => { editor.replaceLines(blockEnd(lines, unit), 0, ['', `${IN}stage ${id}:`, `${IN}${IN}text = ${JSON.stringify(text)}`]); return null; },
    });
  } });

  add('Automatic quests', { label: 'New slot (character, place, item)', code: 'slot …:', run: () => {
    const lines = editor.lines();
    const unit = unitAt(lines, editor.cursorLine());
    if (unit < 0 || !/^template\b/.test(lines[unit])) { status('Put the cursor inside an automatic quest (template)', 'bad'); return; }
    openForm({
      title: 'New slot', help: 'Filled when a character picks the quest. Its name can be used in the conditions, the commands and the texts ({name}).',
      fields: [{ name: 'id', type: 'text', label: 'Name', req: true }, { name: 'type', type: 'select', label: 'What', req: true, options: [['npc', 'Another character'], ['place', 'A place of a character (home, work…)'], ['node', 'A place of the map'], ['item', 'An item']] }],
      ok: ({ id, type }) => {
        if (!/^[A-Za-z_][\w\-]*$/.test(id)) return 'The name can only have letters, digits, _ and -';
        const end = blockEnd(lines, unit);
        let at = end;
        for (let k = unit + 1; k < end; k++) if (/^\s{4}(stage|texts|onStart|onComplete|onFail)\b/.test(lines[k])) { at = k; break; }
        const body = type === 'place' ? ['of = self', 'place = home'] : type === 'npc' ? ['free = true'] : [];
        editor.replaceLines(at, 0, [`${IN}slot ${id}:`, `${IN}${IN}type = ${type}`, ...body.map((l) => IN + IN + l)]);
        return null;
      },
    });
  } });

  for (const [t, label, field] of TRIGGERS) {
    add('Start', { label, code: `trigger = ${t}`, run: () => openForm({
      title: label,
      fields: t === 'talk'
        ? [{ name: 'npc', type: 'id:npc', label: 'Character', req: true }, { name: 'node', type: 'id:node', label: 'Only in this place (optional)' }]
        : [{ name: 'node', type: 'id:node', label: field === 'node' ? 'Place (empty = anywhere)' : 'Place' }],
      okLabel: 'Set',
      ok: (v) => {
        const props = { trigger: t };
        if (v.npc) props.npc = v.npc;
        if (v.node) props.node = v.node;
        setEventProps(props, ['trigger', 'npc', 'node']);
        return null;
      },
    }) });
  }
  add('Start', { label: 'Only once', code: 'once = true', run: () => setEventProps({ once: 'true' }) });
  add('Start', { label: 'Priority (which starts first)', code: 'priority = …', run: () => openForm({
    title: 'Priority', help: 'If several messages can start together, the one with the highest number starts first.',
    fields: [{ name: 'priority', type: 'number', label: 'Priority', req: true }], okLabel: 'Set',
    ok: ({ priority }) => { setEventProps({ priority: String(priority) }); return null; },
  }) });

  for (const [name, spec] of Object.entries(CONDITIONS)) {
    add('Conditions', { label: spec.label, code: `${name}(…)`, run: () => conditionForm(name, spec, (c) => insertCondition([condText(c)])) });
  }
  add('Conditions', { label: 'At least one of these (any)', code: 'any:', run: () => insertCondition(['any:', `${IN}always()`]) });

  add('Dialogue', { label: 'Dialogue line', code: 'who: "text"', run: () => openForm({
    title: 'Dialogue line',
    fields: [{ name: 'who', type: 'id:npc', label: 'Who speaks (empty = no name)' }, { name: 'say', type: 'text', label: 'Text', req: true, long: true }, { name: 'pose', type: 'text', label: 'Pose (happy, angry…, optional)' }],
    ok: (v) => { insertCommand(linesOf({ say: v.say, ...(v.who ? { who: v.who } : {}), ...(v.pose ? { pose: v.pose } : {}) })); return null; },
  }) });
  add('Dialogue', { label: 'Choice (question with answers)', code: 'choice:', run: () => openForm({
    title: 'Choice', help: 'The answers appear with the last dialogue line. Under each answer put the commands to run.',
    fields: [1, 2, 3, 4].map((n) => ({ name: `o${n}`, type: 'text', label: `Answer ${n}`, req: n <= 2 })),
    ok: (v) => {
      const opts = [v.o1, v.o2, v.o3, v.o4].filter(Boolean);
      insertCommand(['choice:', ...opts.flatMap((o) => [`${IN}option ${JSON.stringify(o)}:`, `${IN}${IN}pass`])]);
      return null;
    },
  }) });
  add('Dialogue', { label: 'Add an answer to the choice', code: 'option "…":', run: () => {
    const found = nearestAbove(/^\s*choice\s*:\s*$/);
    if (!found) { status('Put the cursor inside a choice (choice:)', 'bad'); return; }
    openForm({
      title: 'New answer', fields: [{ name: 'text', type: 'text', label: 'Answer text', req: true }],
      ok: ({ text }) => { editor.replaceLines(blockEnd(found.lines, found.k), 0, pad(indentOf(found.lines[found.k]) + 4, [`option ${JSON.stringify(text)}:`, `${IN}pass`])); return null; },
    });
  } });

  add('Flow', { label: 'If… (condition)', code: 'if …:', run: () => pickCondition('If…', (c) => insertCommand([`if ${condText(c)}:`, `${IN}pass`])) });
  add('Flow', { label: 'Otherwise', code: 'else:', run: () => {
    const found = nearestAbove(/^\s*(if|elif)\b.*:\s*$/);
    if (!found) { status('Put the cursor inside an “if”', 'bad'); return; }
    let end = blockEnd(found.lines, found.k);
    const ind = indentOf(found.lines[found.k]);
    while (end < found.lines.length && found.lines[end].trim() && indentOf(found.lines[end]) === ind && /^(elif\b|else\s*:)/.test(found.lines[end].trim())) end = blockEnd(found.lines, end);
    editor.replaceLines(end, 0, pad(ind, ['else:', `${IN}pass`]));
  } });
  add('Flow', { label: 'Nothing (placeholder)', code: 'pass', run: () => insertCommand(['pass']) });

  const byCat = (table, kind) => {
    for (const [name, spec] of Object.entries(table)) {
      if (kind === 'step' && name === 'say') continue;
      add(spec.cat, { label: spec.label, code: `${name}(…)`, run: () => commandForm(name, spec, kind) });
    }
  };
  byCat(STEPS, 'step');
  byCat(EFFECTS, 'effect');
  return cats;
}

const ORDER = ['Structure', 'Start', 'Conditions', 'Dialogue', 'Flow', 'Images', 'Sounds', 'Items', 'Quest', 'Automatic quests', 'Variables', 'World', 'Characters'];
const OPEN = new Set(['Start', 'Conditions', 'Dialogue', 'Flow']);

function renderPalette() {
  const cats = categories();
  const box = $('#palette');
  box.innerHTML = '';
  const filter = $('#filter').value.trim().toLowerCase();
  for (const name of [...ORDER, ...[...cats.keys()].filter((c) => !ORDER.includes(c))]) {
    const items = (cats.get(name) || []).filter((it) => !filter || `${it.label} ${it.code}`.toLowerCase().includes(filter));
    if (!items.length) continue;
    const det = document.createElement('details');
    det.className = 'cat';
    det.open = !!filter || OPEN.has(name);
    det.innerHTML = `<summary>${name}</summary><div class="buttons"></div>`;
    for (const it of items) {
      const b = document.createElement('button');
      b.type = 'button';
      b.innerHTML = `${escapeHtml(it.label)}<small>${escapeHtml(it.code)}</small>`;
      b.addEventListener('mousedown', (e) => e.preventDefault());       // keep the cursor in the code
      b.addEventListener('click', () => { if (!state.doc) { status('Open or create a document first', 'bad'); return; } it.run(); });
      det.querySelector('.buttons').append(b);
    }
    box.append(det);
  }
}

// ====================================================================== the other tabs (Characters, Items, Rules, System)

const recs = createRecords({
  api, query, status, escapeHtml,
  world: () => state.world,
  worldCount: () => $('#world').options.length,
  refs: () => state.refs,
  reloadRefs: async () => { state.refs = await api(`/api/refs?${query({ world: state.world })}`); renderPalette(); },
});

async function showView(view) {
  if (view === state.view) return;
  if (state.view !== 'quests' && !recs.leaveOk()) return;      // a records tab opens again from its first record
  for (const t of document.querySelectorAll('.tab')) t.classList.toggle('active', t.dataset.view === view);
  state.view = view;
  store.set('studio.view', view);
  $('#quests-view').hidden = view !== 'quests';
  $('#records-view').hidden = view === 'quests';
  status('');
  if (view === 'quests') { updateTitle(); return; }
  try { await recs.show(view); } catch (e) { status(`Error: ${e.message}`, 'bad'); }
}

const saveCurrent = () => (state.view === 'quests' ? save() : recs.save());

// ====================================================================== start

$('#save').addEventListener('click', saveCurrent);
for (const t of document.querySelectorAll('.tab')) t.addEventListener('click', () => showView(t.dataset.view));
for (const b of document.querySelectorAll('.rec-mode')) b.addEventListener('click', () => recs.setMode(b.dataset.mode));
$('#rec-delete').addEventListener('click', () => recs.remove());
$('#delete-doc').addEventListener('click', deleteDoc);
$('#new-quest').addEventListener('click', () => startNew('quest'));
$('#new-event').addEventListener('click', () => startNew('event'));
$('#new-template').addEventListener('click', () => startNew('template'));
$('#help').addEventListener('click', () => $('#guide').showModal());
$('#filter').addEventListener('input', renderPalette);
$('#world').addEventListener('change', async (e) => {
  if (!leaveOk() || !recs.leaveOk()) { e.target.value = state.world; return; }
  state.original = editor.value;
  await recs.reload();
  await loadWorld(e.target.value);
  if (state.view !== 'quests') await recs.show(state.view);
});
for (const t of document.querySelectorAll('.ptab')) t.addEventListener('click', () => showTab(t.dataset.tab));
document.addEventListener('keydown', (e) => { if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); saveCurrent(); } });
window.addEventListener('beforeunload', (e) => { if (dirty() || recs.dirty()) e.preventDefault(); });

renderPalette();
loadWorlds()
  .then(() => { const last = store.get('studio.view'); if (last && last !== 'quests' && document.querySelector(`.tab[data-view="${last}"]`)) return showView(last); })
  .catch((e) => status(`Error: ${e.message}`, 'bad'));
