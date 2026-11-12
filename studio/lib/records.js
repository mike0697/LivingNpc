// The records of a world that are not quests or messages: characters (npcs/), shared routines (schedules/), items
// (items/), the rules of the game (rules.json), the title screen (title.json), the bar of pictures (hud.json) and the
// menu of actions on a character (actions.json). Used by the page (the forms) and by the tests (Node).
//
// The JSON stays the only truth: a form edits a copy of the file's object, keys it does not know are kept as they are,
// and the checks below say what Unity would say when it loads the world (ContentLoader / ContentCompiler).

import { toScript, fromScript, condText } from './script.js';

export const ID_RE = /^[A-Za-z0-9_.\-]+$/;
export const DAY_KEYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'];
export const DAY_NAMES = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
export const DAY_WORDS = { all: 'Every day', daily: 'Every day', weekdays: 'Mon-Fri', weekend: 'Sat-Sun' };
export const SEXES = [['m', 'Male'], ['f', 'Female'], ['x', 'Other']];
export const ITEM_TYPES = [
  ['misc', 'Misc'],
  ['consumable', 'Consumable'],
  ['equipment', 'Equipment'],
  ['key', 'Key'],
  ['quest', 'Quest item'],
];

/** The kinds of record, their folder and what a new one looks like. */
export const KINDS = {
  npc: { dir: 'npcs', label: 'Character (NPC)', fresh: (id) => ({ id, name: id, places: { home: '' }, routine: [] }) },
  schedule: { dir: 'schedules', label: 'Shared routine', fresh: (id) => ({ id, blocks: [] }) },
  item: { dir: 'items', label: 'Item', fresh: (id) => ({ id, name: id, type: 'misc', value: 0 }) },
  title: { dir: '', label: 'System', fresh: () => ({ drawTitle: true, musicOn: true }) },
  actions: {
    dir: '', label: 'Actions on a character',
    fresh: () => ({ actions: [{ id: 'talk', label: { en: 'Talk', it: 'Parla' }, do: 'talk' }] }),
  },
  hud: {
    dir: '', label: 'Bar of pictures',
    fresh: () => ({
      align: 'center', size: 64,
      buttons: [
        { id: 'status', label: 'My status', open: 'status', key: 'c' },
        { id: 'people', label: 'Characters', open: 'npcs', key: 'n' },
      ],
    }),
  },
};

/** What an action does besides running its messages (ContentCompiler.Actions). */
export const ACTION_DOS = [['talk', 'Talk (its talk messages)'], ['trade', 'Trade (not in the game yet)']];

/** What a button of the bar can open (ContentCompiler.Hud: CompiledHud.Screens). */
export const HUD_SCREENS = [['status', 'My status (the player)'], ['npcs', 'Characters (list and page)'], ['journal', 'Journal panel'], ['backpack', 'Backpack panel'], ['present', 'Present panel']];
/** What the characters screen can say besides name, picture, description, stats and hints (CompiledHud.Details). */
export const HUD_DETAILS = [['age', 'Age'], ['activity', 'What they are doing'], ['where', 'Where they are'], ['relation', 'What they think of you'], ['goal', 'Their goal']];
/** Keys a button can use: the game keeps F5, F6, F9, F12, Esc, Space and Enter for itself. */
export const HUD_KEY_RE = /^(?:[A-Za-z]|[0-9]|F(?:[1-4]|7|8|10|11))$/;

const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

/** JSON as the game's files are written: two spaces, LF, a final newline. */
export const jsonText = (o) => JSON.stringify(o, null, 2) + '\n';

/** A localized text ("Elena" or {"it": "Elena", "en": "Elena"}) in the first language. */
export const textOf = (v, lang = 'it') => (typeof v === 'string' ? v : isObj(v) ? String(v[lang] ?? Object.values(v)[0] ?? '') : '');

// ====================================================================== time and days

/** "HH:mm" -> minutes, as GameTime.TryParse (hours 0-24, minutes 0-59); null when not valid. */
export function parseTime(s) {
  if (typeof s !== 'string') return null;
  const m = /^\s*(\d+):(\d+)\s*$/.exec(s);
  if (!m) return null;
  const h = Number(m[1]), min = Number(m[2]);
  if (h > 24 || min > 59) return null;
  return h * 60 + min;
}

export const clock = (minutes) => `${String(Math.floor(minutes / 60)).padStart(2, '0')}:${String(minutes % 60).padStart(2, '0')}`;

/** Days of a block ("weekdays", "mon", ["mon", "wed"], missing = all) -> bitmask (bit 0 = Monday); -1 when a name is unknown. */
export function dayMask(days) {
  if (days === undefined || days === null) return 0x7f;
  let mask = 0;
  for (const n of Array.isArray(days) ? days : [days]) {
    const s = String(n).trim().toLowerCase();
    if (s === 'all' || s === 'daily') mask |= 0x7f;
    else if (s === 'weekdays') mask |= 0x1f;
    else if (s === 'weekend') mask |= 0x60;
    else if (DAY_KEYS.includes(s)) mask |= 1 << DAY_KEYS.indexOf(s);
    else return -1;
  }
  return mask;
}

/** A bitmask back to the shortest way the files write it. */
export function daysOf(mask) {
  if (mask === 0x7f) return 'all';
  if (mask === 0x1f) return 'weekdays';
  if (mask === 0x60) return 'weekend';
  const list = DAY_KEYS.filter((_, i) => mask & (1 << i));
  return list.length === 1 ? list[0] : list;
}

const rotateNextDay = (mask) => ((mask << 1) | (mask >> 6)) & 0x7f;

// ====================================================================== routines

/**
 * The blocks of an npc's routine as the game uses them (ContentCompiler.AddBlock): shared schedules expanded, blocks that
 * cross midnight split, the tail on the following day. Each: { mask, start, end, at, place, activity, priority, id, from }.
 * Blocks that the game would skip (bad time, no at...) are left out.
 */
export function routineBlocks(routine, schedules = {}) {
  const out = [];
  const add = (b, from) => {
    if (!isObj(b)) return;
    const start = parseTime(b.from ?? '00:00'), end = parseTime(b.to ?? '24:00');
    if (start === null || end === null || start === end) return;
    const mask = dayMask(b.days);
    if (mask <= 0 || typeof b.at !== 'string' || !b.at) return;
    const base = { at: b.at, place: b.at.startsWith('$') ? b.at.slice(1) : null, activity: b.activity || '', priority: Number(b.priority) || 0, id: b.id || '', from };
    if (end > start) out.push({ ...base, mask, start, end });
    else {
      out.push({ ...base, mask, start, end: 1440 });
      if (end > 0) out.push({ ...base, mask: rotateNextDay(mask), start: 0, end });
    }
  };
  for (const entry of routine || []) {
    if (typeof entry === 'string' || (isObj(entry) && entry.schedule !== undefined)) {
      const sid = typeof entry === 'string' ? entry : entry.schedule;
      for (const b of schedules[sid]?.blocks || []) add(b, `shared routine ${sid}`);
    } else add(entry, 'routine');
  }
  return out;
}

/**
 * Where an npc is on a day of the week (0 = Monday) at a minute of the day, as NpcLogic.Resolve: the block with the
 * highest priority wins (the first one on a tie); a "$place" block goes where the place points, and is skipped when the
 * npc has no such place; with no block the npc is at home. Returns { node, activity, block }.
 */
export function whereAt(npc, schedules, dow, minute) {
  const places = isObj(npc?.places) ? npc.places : {};
  const blocks = routineBlocks(npc?.routine, schedules);
  let best = -Infinity, found = null, node = places.home || blocks[0]?.at || '', activity = '';
  for (const b of blocks) {
    if (!((b.mask >> dow) & 1) || minute < b.start || minute >= b.end) continue;
    const target = b.place ? places[b.place] : b.at;
    if (!target) continue;
    if (b.priority > best) { best = b.priority; node = target; activity = b.activity; found = b; }
  }
  return { node, activity, block: found };
}

// ====================================================================== checks

/** The ids of the world the checks need: { node, item, stat, activity, schedule, npc, images, poses } (arrays). */
const has = (list, v) => (list || []).includes(v);

function idProblems(json, kindLabel, taken, P) {
  if (!isObj(json)) { P.err('the file must contain a JSON object { … }'); return; }
  if (typeof json.id !== 'string' || !json.id) P.err(`${kindLabel} without "id"`);
  else if (!ID_RE.test(json.id)) P.err(`“${json.id}”: an id can only have letters, digits, _ - . (it becomes the file name)`);
  else if (taken && taken.includes(json.id)) P.err(`“${json.id}” already exists`);
}

function blockProblems(b, where, refs, places, P) {
  if (!isObj(b)) { P.err(`${where}: must be a block { … } or the name of a shared routine`); return; }
  const start = parseTime(b.from ?? '00:00'), end = parseTime(b.to ?? '24:00');
  if (start === null || end === null) P.err(`${where}: invalid time “${b.from ?? ''}”-“${b.to ?? ''}” (write HH:mm)`);
  else if (start === end) P.warn(`${where}: from ${b.from} to ${b.to} is an empty interval, the game skips it`);
  if (dayMask(b.days) < 0) P.err(`${where}: invalid days ${JSON.stringify(b.days)} (mon…sun, weekdays, weekend, all)`);
  if (typeof b.at !== 'string' || !b.at) P.err(`${where}: the place (at) is missing`);
  else if (b.at.startsWith('$')) {
    if (places && !(b.at.slice(1) in places)) P.warn(`${where}: “${b.at}” is not one of the character's places (fine only if a setPlace sets it)`);
  } else if (refs.node && !has(refs.node, b.at)) P.err(`${where}: the place “${b.at}” does not exist`);
  if (b.activity && refs.activity && !has(refs.activity, b.activity)) P.warn(`${where}: the activity “${b.activity}” is not in rules.json (it will not affect the stats)`);
  if (b.priority !== undefined && !Number.isFinite(Number(b.priority))) P.err(`${where}: the priority must be a number`);
}

function imageProblems(img, where, refs, P) {
  if (!isObj(img)) { P.err(`${where}: must be { "file": … }`); return; }
  if (img.file && refs.images && !has(refs.images, img.file)) P.warn(`${where}: the image “${img.file}” is not in the images folder`);
  if (!img.file && !isObj(img.nodes)) P.warn(`${where}: the image file is missing`);
  for (const k of ['x', 'y', 'width', 'height']) if (img[k] !== undefined && !Number.isFinite(img[k])) P.err(`${where}: ${k} must be a number`);
  if (isObj(img.nodes)) for (const [node, o] of Object.entries(img.nodes)) {
    if (refs.node && !has(refs.node, node)) P.warn(`${where}, at the place “${node}”: the place does not exist`);
    if (isObj(o) && o.file && refs.images && !has(refs.images, o.file)) P.warn(`${where}, at the place “${node}”: the image “${o.file}” is not in the images folder`);
  }
}

function heldProblems(inv, where, refs, P) {
  if (inv === undefined) return;
  if (!isObj(inv)) { P.err(`${where}: must be { "item": amount }`); return; }
  for (const [item, n] of Object.entries(inv)) {
    if (refs.item && !has(refs.item, item)) P.err(`${where}: the item “${item}” does not exist`);
    if (!Number.isInteger(n) || n <= 0) P.warn(`${where}: the amount of “${item}” must be a whole number greater than 0 (the game skips it)`);
  }
}

function collector() {
  const errors = [], warnings = [];
  return { errors, warnings, err: (m) => errors.push(m), warn: (m) => warnings.push(m) };
}

/** Problems of a record: { errors: [text], warnings: [text] }. taken: ids already used by other records of the kind (for a new one). */
export function checkRecord(kind, json, refs = {}, taken = null) {
  const P = collector();
  if (kind === 'npc') checkNpc(json, refs, taken, P);
  else if (kind === 'schedule') checkSchedule(json, refs, taken, P);
  else if (kind === 'item') checkItem(json, refs, taken, P);
  else if (kind === 'rules') checkRules(json, refs, P);
  else if (kind === 'title') checkTitle(json, refs, P);
  else if (kind === 'hud') checkHud(json, refs, P);
  else if (kind === 'actions') checkActions(json, refs, P);
  return { errors: P.errors, warnings: P.warnings };
}

/** A condition written in a file: what the script language says of it (unknown ids, wrong commands). self: "self" is a character. */
function condProblems(c, where, refs, P, self = false) {
  if (c === undefined || c === null) return;
  const { errors, warnings } = condFromText(condToText(c), refs, { self });
  for (const e of errors) P.err(`${where}: ${e}`);
  for (const w of warnings) P.warn(`${where}: ${w}`);
}

/** hud.json as ContentCompiler.CompileHud reads it: what it skips or replaces is a warning here, what it refuses an error. */
function checkHud(hud, refs, P) {
  if (!isObj(hud)) { P.err('hud.json must contain a JSON object { … }'); return; }
  if (hud.align !== undefined && !['left', 'center', 'centre', 'right'].includes(String(hud.align).toLowerCase())) P.warn(`align “${hud.align}”: write left, center or right (the game uses center)`);
  if (hud.size !== undefined && (!Number.isFinite(hud.size) || hud.size < 24 || hud.size > 160)) P.warn('size: between 24 and 160 pixels (the game uses 64)');
  const IMG = /\.(png|jpe?g|webp)$/i;
  const picture = (value, where) => {
    if (value === undefined || value === null || value === '') return;
    if (typeof value !== 'string') { P.err(`${where}: must be a file name`); return; }
    if (value.split(/[\\/]/).includes('..')) { P.err(`${where}: “${value}” cannot point outside the pack`); return; }
    if (!IMG.test(value)) { P.warn(`${where}: “${value}” must be png, jpg or webp`); return; }
    if (refs.images && !has(refs.images, value.replace(/\\/g, '/'))) P.warn(`${where}: “${value}” is not in the images folder`);
  };
  if (hud.buttons !== undefined && !Array.isArray(hud.buttons)) P.err('buttons must be a list');
  const buttons = Array.isArray(hud.buttons) ? hud.buttons : [];
  if (!buttons.length) P.warn('the bar has no buttons');
  const ids = new Set(), keys = new Set();
  buttons.forEach((b, i) => {
    if (!isObj(b)) { P.err(`button ${i + 1}: must be { "id": … }`); return; }
    const where = `button ${b.id || i + 1}`;
    if (typeof b.id !== 'string' || !b.id.trim()) { P.err(`button ${i + 1}: the id is missing`); return; }
    if (!ID_RE.test(b.id)) P.err(`${where}: an id can only have letters, digits, _ - .`);
    if (ids.has(b.id)) P.err(`${where}: two buttons have this id (the second replaces the first)`);
    ids.add(b.id);
    if (String(b.$op || '').toLowerCase() === 'delete') return;
    if (b.icon) picture(b.icon, `${where}, picture`);
    else P.warn(`${where}: no picture, the game shows a button with its label`);
    if (b.open !== undefined && b.open !== '') {
      const open = String(b.open).toLowerCase();
      if (open === 'map') P.warn(`${where}: the map screen does not exist yet, the button only runs its messages`);
      else if (!HUD_SCREENS.some(([s]) => s === open)) P.err(`${where}: open “${b.open}” is not a screen (${HUD_SCREENS.map(([s]) => s).join(', ')})`);
    }
    if (b.key !== undefined && b.key !== '') {
      if (!HUD_KEY_RE.test(String(b.key).trim())) P.err(`${where}: key “${b.key}”: a letter, a digit, F1-F4, F7, F8, F10 or F11 (the other keys belong to the game)`);
      else if (keys.has(String(b.key).trim().toUpperCase())) P.warn(`${where}: key “${b.key}” is used by another button too (only the first one gets it)`);
      keys.add(String(b.key).trim().toUpperCase());
    }
    condProblems(b.when, `${where}, shown when`, refs, P);
  });
  const stats = (list, where) => {
    if (list === undefined) return;
    if (!Array.isArray(list)) { P.err(`${where}: must be a list of stat ids`); return; }
    for (const s of list) if (refs.stat && !has(refs.stat, s)) P.warn(`${where}: the stat “${s}” is not in rules.json`);
  };
  if (hud.status !== undefined && !isObj(hud.status)) P.err('status must be { … }');
  if (isObj(hud.status)) { picture(hud.status.image, 'my status, picture'); stats(hud.status.stats, 'my status, stats'); }
  if (hud.npcs !== undefined && !isObj(hud.npcs)) P.err('npcs must be { … }');
  if (isObj(hud.npcs)) {
    stats(hud.npcs.stats, 'characters, stats');
    if (hud.npcs.show !== undefined && !Array.isArray(hud.npcs.show)) P.err('characters, show: must be a list');
    else for (const d of hud.npcs.show || []) if (!HUD_DETAILS.some(([k]) => k === d)) P.warn(`characters, show: “${d}” is not known (${HUD_DETAILS.map(([k]) => k).join(', ')})`);
  }
}

/** actions.json as ContentCompiler.CompileActions reads it (ids are shared by the sub-actions, at any depth). */
function checkActions(a, refs, P) {
  if (!isObj(a)) { P.err('actions.json must contain a JSON object { "actions": [ … ] }'); return; }
  if (a.actions !== undefined && !Array.isArray(a.actions)) { P.err('actions must be a list'); return; }
  if (!(a.actions || []).length) P.warn('there are no actions: clicking a character opens an empty menu');
  if (a.pageSize !== undefined && (!Number.isInteger(a.pageSize) || a.pageSize < 0)) P.warn('pageSize: a whole number, 0 = all on one page');
  if (a.labels !== undefined) {
    if (!isObj(a.labels)) P.err('labels must be { "next": …, "back": …, "close": … }');
    else for (const k of Object.keys(a.labels)) if (!['next', 'back', 'close'].includes(k.toLowerCase())) P.warn(`labels.${k}: unknown (next, back, close)`);
  }
  checkActionList(a.actions || [], new Set(), refs, P, 0, '');
}

function checkActionList(list, ids, refs, P, depth, inside) {
  list.forEach((x, i) => {
    if (!isObj(x)) { P.err(`${inside}action ${i + 1}: must be { "id": … }`); return; }
    const where = `${inside}action ${x.id || i + 1}`;
    if (typeof x.id !== 'string' || !x.id.trim()) { P.err(`action ${i + 1}: the id is missing`); return; }
    if (!ID_RE.test(x.id)) P.err(`${where}: an id can only have letters, digits, _ - .`);
    if (ids.has(x.id)) P.err(`${where}: two actions have this id (also counting the sub-actions)`);
    ids.add(x.id);
    if (String(x.$op || '').toLowerCase() === 'delete') return;
    if (x.icon && refs.images && !has(refs.images, x.icon)) P.warn(`${where}: the picture “${x.icon}” is not in the images folder`);
    if (x.actions !== undefined) {
      if (!Array.isArray(x.actions)) P.err(`${where}: the sub-actions must be a list`);
      else if (depth >= 3) P.err(`${where}: sub-actions go at most 4 levels deep`);
      else {
        if (!x.actions.length) P.warn(`${where}: it has no sub-actions, it is never offered`);
        if (x.do) P.warn(`${where}: an action with sub-actions only opens them, “does” is ignored`);
        checkActionList(x.actions, ids, refs, P, depth + 1, `${x.id} › `);
      }
      if (x.pageSize !== undefined && (!Number.isInteger(x.pageSize) || x.pageSize < 0)) P.warn(`${where}: pageSize: a whole number, 0 = all on one page`);
    } else if (x.pageSize !== undefined) P.warn(`${where}: pageSize is used only with sub-actions`);
    if (x.do !== undefined && x.do !== '') {
      const d = String(x.do).toLowerCase();
      if (d === 'trade') P.warn(`${where}: trading does not exist yet, the action only runs its messages`);
      else if (d !== 'talk') P.err(`${where}: do “${x.do}” is not known (talk, trade; empty = only its messages)`);
    }
    for (const key of ['npcs', 'tags']) if (x[key] !== undefined && !(Array.isArray(x[key]) && x[key].every((v) => typeof v === 'string'))) P.err(`${where}: ${key} must be a list of words`);
    for (const n of Array.isArray(x.npcs) ? x.npcs : []) if (refs.npc && !has(refs.npc, n)) P.warn(`${where}: the character “${n}” does not exist`);
    condProblems(x.when, `${where}, offered when`, refs, P, true);
  });
}

/** The page of a character in the characters screen of the bar (npc "profile"). */
function profileProblems(p, refs, P) {
  if (p === undefined) return;
  if (!isObj(p)) { P.err('profile must be { "image": …, "when": …, "hints": [ … ] }'); return; }
  if (p.image !== undefined && p.image !== '') {
    if (typeof p.image !== 'string') P.err('profile picture: must be a file name');
    else if (refs.images && !has(refs.images, p.image)) P.warn(`profile picture: “${p.image}” is not in the images folder`);
  }
  condProblems(p.when, 'profile, listed when', refs, P);
  if (p.hints !== undefined && !Array.isArray(p.hints)) { P.err('profile hints: must be a list'); return; }
  (p.hints || []).forEach((hint, i) => {
    const where = `hint ${i + 1}`;
    if (typeof hint === 'string') { if (!hint.trim()) P.warn(`${where}: it is empty`); return; }
    if (!isObj(hint)) { P.err(`${where}: must be a text or { "text": …, "when": … }`); return; }
    if (hint.text === undefined) return;          // a text in several languages
    if (typeof hint.text !== 'string' && !isObj(hint.text)) P.err(`${where}: the text must be a text`);
    condProblems(hint.when, `${where}, shown when`, refs, P);
  });
}

/** Colours Unity's ColorUtility.TryParseHtmlString reads: #rgb, #rrggbb, #rgba, #rrggbbaa (and a few names). */
export const COLOR_RE = /^#([0-9a-f]{3}|[0-9a-f]{4}|[0-9a-f]{6}|[0-9a-f]{8})$/i;
const COLOR_NAMES = ['red', 'cyan', 'blue', 'darkblue', 'lightblue', 'purple', 'yellow', 'lime', 'fuchsia', 'white', 'silver', 'grey', 'black', 'orange', 'brown', 'maroon', 'green', 'olive', 'navy', 'teal', 'aqua', 'magenta'];

/** title.json as ContentLoader.LoadTitleScreen reads it: what it skips (and says in the menu) is a warning here. */
function checkTitle(t, refs, P) {
  if (!isObj(t)) { P.err('title.json must contain a JSON object { … }'); return; }
  const file = (key, value, list, exts, what) => {
    if (value === undefined || value === null || value === '') return;
    if (typeof value !== 'string') { P.err(`${key}: must be a file name`); return; }
    if (value.split(/[\\/]/).includes('..')) { P.err(`${key}: “${value}” cannot point outside the pack`); return; }
    if (!exts.test(value)) { P.err(`${key}: “${value}” must be ${what}`); return; }
    if (list && !list.includes(value.replace(/\\/g, '/'))) P.warn(`${key}: “${value}” is not there (the game reports it in the menu and goes on without it)`);
  };
  const IMG = /\.(png|jpe?g|webp)$/i, SND = /\.(mp3|ogg|wav)$/i;
  file('background image', t.image, refs.images, IMG, 'png, jpg or webp');
  for (const k of ['drawTitle', 'musicOn']) if (t[k] !== undefined && typeof t[k] !== 'boolean') P.err(`${k}: must be true or false`);
  if (t.musicOn !== false) file('music', t.music, refs.music, SND, 'mp3, ogg or wav');
  if (t.musicOn === true && !t.music) P.warn('music is on but no file is chosen');
  if (t.volume !== undefined && (typeof t.volume !== 'number' || t.volume < 0 || t.volume > 1)) P.warn('volume: goes from 0 to 1 (the game brings it within these limits)');
  for (const [key, label] of [['buttons', 'game buttons'], ['menuButtons', 'menu buttons']]) {
    const b = t[key];
    if (b === undefined || b === null) continue;
    if (!isObj(b)) { P.err(`${label}: must be { "normal": …, "hover": … }`); continue; }
    if (!b.normal) P.warn(`${label}: the normal image is missing, the game uses ${key === 'menuButtons' ? 'the game buttons' : 'the plain coloured buttons'}`);
    file(`${label}, normal image`, b.normal, refs.images, IMG, 'png, jpg or webp');
    file(`${label}, hover / pressed image`, b.hover, refs.images, IMG, 'png, jpg or webp');
    if (b.border !== undefined && (!Number.isInteger(b.border) || b.border < 0)) P.err(`${label}: the ends (border) must be a whole number from 0 up`);
    if (b.textColor !== undefined && !(typeof b.textColor === 'string' && (COLOR_RE.test(b.textColor.trim()) || COLOR_NAMES.includes(b.textColor.trim().toLowerCase())))) P.warn(`${label}: “${b.textColor}” is not a colour (write for example "#4a2516"); the game uses white`);
  }
}

function checkNpc(npc, refs, taken, P) {
  idProblems(npc, 'character', taken, P);
  if (!isObj(npc)) return;
  const places = npc.places === undefined ? {} : npc.places;
  if (!isObj(places)) P.err('the places must be { "name": "place" }');
  else {
    for (const [name, node] of Object.entries(places)) {
      if (!name) P.err('a place of the character has no name');
      if (typeof node !== 'string' || !node) P.err(`place “${name}”: the map place is missing`);
      else if (refs.node && !has(refs.node, node)) P.err(`place “${name}”: “${node}” does not exist`);
    }
    if (!places.home) P.warn('the place “home” is missing: the game uses the first place of the routine (or the start place) as home');
  }
  if (npc.routine !== undefined && !Array.isArray(npc.routine)) P.err('the routine must be a list');
  (Array.isArray(npc.routine) ? npc.routine : []).forEach((entry, i) => {
    const where = `routine, line ${i + 1}`;
    if (typeof entry === 'string' || (isObj(entry) && entry.schedule !== undefined)) {
      const sid = typeof entry === 'string' ? entry : entry.schedule;
      if (refs.schedule && !has(refs.schedule, sid)) P.err(`${where}: the shared routine “${sid}” does not exist`);
    } else blockProblems(entry, where, refs, isObj(places) ? places : {}, P);
  });
  if (npc.stats !== undefined) {
    if (!isObj(npc.stats)) P.err('the stats must be { "stat": value }');
    else for (const [s, v] of Object.entries(npc.stats)) {
      if (refs.stat && !has(refs.stat, s)) P.warn(`stat “${s}”: not in rules.json`);
      if (!Number.isFinite(v)) P.err(`stat “${s}”: the value must be a number`);
    }
  }
  heldProblems(npc.inventory, 'inventario', refs, P);
  profileProblems(npc.profile, refs, P);
  for (const k of ['firstName', 'lastName']) if (npc[k] !== undefined && typeof npc[k] !== 'string' && !isObj(npc[k])) P.err(`${k}: must be a text`);
  if (npc.age !== undefined && (!Number.isInteger(npc.age) || npc.age < 0)) P.err('age: must be a whole number from 0 up');
  if (npc.sex !== undefined && !SEXES.some(([v]) => v === npc.sex)) P.warn(`sex “${npc.sex}”: the game knows m, f and x`);
  if (npc.autoQuests !== undefined && typeof npc.autoQuests !== 'boolean') P.err('autoQuests: must be true or false');
  if (npc.goal !== undefined && npc.goal !== '' && refs.goal && !has(refs.goal, npc.goal)) P.err(`goal “${npc.goal}”: not in rules.json (Rules tab, Goals)`);
  if (npc.relations !== undefined) {
    if (!isObj(npc.relations)) P.err('the relations must be { "character": value }');
    else for (const [who, v] of Object.entries(npc.relations)) {
      if (who !== 'player' && refs.npc && !has(refs.npc, who)) P.err(`relation with “${who}”: the character does not exist`);
      if (who === npc.id) P.warn('a relation with itself is ignored');
      if (!Number.isFinite(v)) P.err(`relation with “${who}”: the value must be a number`);
      else if (v < -100 || v > 100) P.warn(`relation with “${who}”: goes from -100 to 100`);
    }
  }
  if (npc.tags !== undefined && !(Array.isArray(npc.tags) && npc.tags.every((t) => typeof t === 'string'))) P.err('the tags must be a list of words');
  const imgs = npc.images;
  if (imgs !== undefined) {
    if (!isObj(imgs)) P.err('images must be { "default": …, "activity": …, "talk": … }');
    else {
      if (imgs.default !== undefined) imageProblems(imgs.default, 'image on the map', refs, P);
      for (const group of ['activity', 'talk']) {
        if (imgs[group] === undefined) continue;
        if (!isObj(imgs[group])) { P.err(`images.${group} must be { "name": { "file": … } }`); continue; }
        for (const [k, img] of Object.entries(imgs[group])) {
          imageProblems(img, group === 'activity' ? `image while doing “${k}”` : `image when talking (pose “${k}”)`, refs, P);
          if (group === 'activity' && refs.activity && !has(refs.activity, k)) P.warn(`image while doing “${k}”: the activity is not in rules.json`);
        }
      }
    }
  }
}

function checkSchedule(s, refs, taken, P) {
  idProblems(s, 'shared routine', taken, P);
  if (!isObj(s)) return;
  if (!Array.isArray(s.blocks)) { P.err('a shared routine has the list "blocks"'); return; }
  if (!s.blocks.length) P.warn('the routine has no blocks');
  s.blocks.forEach((b, i) => blockProblems(b, `blocco ${i + 1}`, refs, null, P));
}

function checkItem(item, refs, taken, P) {
  idProblems(item, 'item', taken, P);
  if (!isObj(item)) return;
  if (item.type !== undefined && !ITEM_TYPES.some(([t]) => t === String(item.type).toLowerCase())) P.warn(`unknown type “${item.type}”: the game uses “misc” (${ITEM_TYPES.map(([t]) => t).join(', ')})`);
  if (item.value !== undefined && (!Number.isFinite(item.value) || item.value < 0)) P.warn('the value must be a number from 0 up (the game uses 0)');
  if (item.tags !== undefined && !(Array.isArray(item.tags) && item.tags.every((t) => typeof t === 'string'))) P.err('the tags must be a list of words');
}

function checkRules(r, refs, P) {
  if (!isObj(r)) { P.err('rules.json must contain a JSON object { … }'); return; }
  const int = (k, min, max, label) => {
    if (r[k] === undefined) return;
    if (!Number.isInteger(r[k]) || r[k] < min || (max !== undefined && r[k] > max)) P.err(`${label}: must be a whole number${max !== undefined ? ` from ${min} to ${max}` : ` from ${min} up`}`);
  };
  int('tickMinutes', 1, undefined, 'minutes per step');
  int('startMinute', 0, 1439, 'start time');
  int('defaultLinkMinutes', 0, undefined, 'minutes of a link');
  if (r.startDayOfWeek !== undefined && !Number.isInteger(r.startDayOfWeek)) P.err('start day: must be a whole number (0 = Monday)');
  const stats = Array.isArray(r.stats) ? r.stats : [];
  if (r.stats !== undefined && !Array.isArray(r.stats)) P.err('stats must be a list');
  const seen = new Set();
  stats.forEach((s, i) => {
    const where = `stat ${s?.id || i + 1}`;
    if (!isObj(s) || !s.id) { P.err(`stat ${i + 1}: the id is missing`); return; }
    if (seen.has(s.id)) P.err(`${where}: id ripetuto`);
    seen.add(s.id);
    for (const k of ['min', 'max', 'baseline', 'driftPerHour', 'constantPerHour']) if (s[k] !== undefined && !Number.isFinite(s[k])) P.err(`${where}: ${k} must be a number`);
    const min = s.min ?? 0, max = s.max ?? 100, base = s.baseline ?? 50;
    if (min > max) P.err(`${where}: the minimum is greater than the maximum`);
    else if (base < min || base > max) P.warn(`${where}: the starting value ${base} is outside ${min}-${max}`);
  });
  const statIds = stats.map((s) => s?.id).filter(Boolean);
  const acts = Array.isArray(r.activities) ? r.activities : [];
  const seenA = new Set();
  acts.forEach((a, i) => {
    const id = typeof a === 'string' ? a : a?.id;
    if (!id) { P.err(`activity ${i + 1}: the id is missing`); return; }
    if (seenA.has(id)) P.err(`activity ${id}: repeated id`);
    seenA.add(id);
    if (isObj(a) && isObj(a.effects)) for (const [s, v] of Object.entries(a.effects)) {
      if (!statIds.includes(s)) P.warn(`activity ${id}: the stat “${s}” does not exist`);
      if (!Number.isFinite(v)) P.err(`activity ${id}: the effect on “${s}” must be a number`);
    }
  });
  (Array.isArray(r.dayParts) ? r.dayParts : []).forEach((d, i) => {
    const where = `daypart ${d?.id || i + 1}`;
    if (!isObj(d)) { P.err(`${where}: must be an object`); return; }
    if (parseTime(d.from ?? '00:00') === null || parseTime(d.to ?? '24:00') === null) P.err(`${where}: invalid time (write HH:mm)`);
  });
  if (r.sleep !== undefined) {
    if (!isObj(r.sleep)) P.err('sleep must be an object');
    else {
      if (r.sleep.from !== undefined && parseTime(r.sleep.from) === null) P.err('sleep: “from” is not a valid time');
      if (r.sleep.wakeAt !== undefined && parseTime(r.sleep.wakeAt) === null) P.err('sleep: “wake at” is not a valid time');
    }
  }
  heldProblems(r.playerInventory, 'inventario iniziale', refs, P);
  const seenG = new Set();
  if (r.goals !== undefined && !Array.isArray(r.goals)) P.err('goals must be a list');
  (Array.isArray(r.goals) ? r.goals : []).forEach((g, i) => {
    if (!isObj(g) || !g.id) { P.err(`goal ${i + 1}: the id is missing`); return; }
    if (!ID_RE.test(g.id)) P.err(`goal ${g.id}: an id can only have letters, digits, _ - .`);
    if (seenG.has(g.id)) P.err(`goal ${g.id}: repeated id`);
    seenG.add(g.id);
  });
  const a = r.autoQuests;
  if (a !== undefined) {
    if (!isObj(a)) P.err('autoQuests must be an object');
    else {
      if (a.checkMinutes !== undefined && (!Number.isInteger(a.checkMinutes) || a.checkMinutes < 1)) P.err('automatic quests, check every: a whole number of minutes from 1 up');
      if (a.maxActive !== undefined && (!Number.isInteger(a.maxActive) || a.maxActive < 0)) P.err('automatic quests, at most: a whole number from 0 up');
      if (a.pauseDays !== undefined && (!Number.isFinite(a.pauseDays) || a.pauseDays < 0)) P.err('automatic quests, pause: a number of days from 0 up');
    }
  }
}

// ====================================================================== effects as script lines

/** A list of effects as the lines of the script language (one command per line, no indentation). */
export function effectsToText(list) {
  if (list === undefined || list === null) return '';
  const text = toScript([{ kind: 'quest', json: { id: '_', onStart: Array.isArray(list) ? list : [list] } }]);
  return text.split('\n').filter((l) => l.startsWith('        ')).map((l) => l.slice(8)).filter((l) => l !== 'pass').join('\n');
}

/** Script lines back to a list of effects: { effects, errors: [text] } (errors with their line). */
export function effectsFromText(text, refs) {
  const lines = String(text).split('\n');
  if (!lines.some((l) => l.trim())) return { effects: [], errors: [] };
  // inside a quest of one stage, the lines keep their number + 2
  const script = ['quest _:', '    onStart:', ...lines.map((l) => (l.trim() ? '        ' + l : '')), '    stage _:', '        text = "_"'].join('\n');
  const r = fromScript(script, refs);
  const errors = [...r.errors, ...r.warnings].map((e) => `line ${e.line - 2}: ${e.message}`);
  return { effects: r.units[0]?.json?.onStart ?? [], errors: r.errors.length ? errors : [], warnings: r.errors.length ? [] : errors };
}

// ====================================================================== conditions as script lines

/** A condition as lines of the script language: one line per condition of the top "all", e.g. quest(crypt, active). */
export function condToText(c) {
  if (c === undefined || c === null) return '';
  const top = Array.isArray(c) ? c : isObj(c) && Object.keys(c).length === 1 && Array.isArray(c.all) ? c.all : [c];
  return top.map(condText).join('\n');
}

/**
 * Script lines back to a condition: { cond (undefined when empty), errors, warnings } (texts with their line).
 * self: the condition is about a character clicked (actions), so "self" stands for it.
 */
export function condFromText(text, refs, { self = false } = {}) {
  const lines = String(text ?? '').split('\n');
  if (!lines.some((l) => l.trim())) return { cond: undefined, errors: [], warnings: [] };
  // the "when" of a message (of an action message when self is a character): the lines keep their number + 3
  const head = self ? ['event _:', '    trigger = action', '    when:'] : ['event _:', '    trigger = enterNode', '    when:'];
  const script = [...head, ...lines.map((l) => (l.trim() ? '        ' + l.trim() : '')), ''].join('\n');
  const r = fromScript(script, refs);
  if (self) r.errors = r.errors.filter((e) => !/needs action =/.test(e.message));
  const say = (e) => (lines.length > 1 ? `line ${e.line - 3}: ${e.message}` : e.message);
  // a line with a mistake is skipped, so the block may also be "empty": that second message says nothing more
  const errors = r.errors.filter((e, _, all) => all.length === 1 || !/has no conditions/.test(e.message));
  return { cond: r.errors.length ? undefined : r.units[0]?.json?.when, errors: errors.map(say), warnings: r.warnings.map(say) };
}

// ====================================================================== where an id is used

const ID_KEYS = {
  quest: ['quest'],
  template: ['template'],
  item: ['item'],
  npc: ['npc', 'who', 'giver', 'target'],
  schedule: ['schedule'],
};

/**
 * The places that use an id: files is [{ rel, json }] (quests, events, npcs, schedules, items, rules). Returns
 * [{ rel, where }] with a short description of each use.
 */
export function usages(kind, id, files) {
  const out = [];
  const keys = ID_KEYS[kind] || [];
  for (const { rel, json } of files) {
    const found = new Set();
    const scan = (v, path) => {
      if (Array.isArray(v)) { v.forEach((x, i) => scan(x, `${path}[${i}]`)); return; }
      if (!isObj(v)) return;
      for (const [k, x] of Object.entries(v)) {
        if (keys.includes(k) && x === id) found.add(describe(v, k));
        scan(x, path ? `${path}.${k}` : k);
      }
    };
    scan(json, '');
    if (kind === 'item') {
      if (rel.startsWith('npcs/') && isObj(json?.inventory) && id in json.inventory) found.add('in the character\'s inventory');
      if (rel === 'rules.json' && isObj(json?.playerInventory) && id in json.playerInventory) found.add('in the player\'s starting inventory');
    }
    if (kind === 'schedule' && rel.startsWith('npcs/') && Array.isArray(json?.routine) && json.routine.includes(id)) found.add('in the routine');
    for (const where of found) out.push({ rel, where });
  }
  return out;
}

function describe(obj, key) {
  if (obj.do) return `command ${obj.do}`;
  if (obj.type && typeof obj.type === 'string' && key !== 'type') return `condition ${obj.type}`;
  if (key === 'who') return 'dialogue line';
  if (key === 'giver') return 'quest giver';
  if (key === 'npc' && obj.trigger) return 'message when you talk to them';
  if (key === 'schedule') return 'routine';
  return key;
}
