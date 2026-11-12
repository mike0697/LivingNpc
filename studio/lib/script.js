// The script language: JSON of quests and events <-> text that reads like Python.
//
//   quest q_chest:                      event ev_chest_open:
//       title = "The chest"                 trigger = enterNode
//       autoStart:                          node = camera
//           playerAt(casa_1)                when:
//       stage open:                             quest(q_chest, active, open)
//           text = "Open the chest"         elena: "Here it is."
//                                           show(chest, "chest.png", x=40, y=30)
//                                           choice:
//                                               option "Open it":
//                                                   giveItem(gold_coin, 100)
//                                                   completeQuest(q_chest)
//                                               option "Leave it":
//                                                   pass
//
//   template t_bread:                   lines greet:
//       title = "{self} brings bread"       variant if relation(self, npc, ">", 20):
//       slot friend:                            "Hello, {npc}!"
//           type = npc                      variant:
//       texts:                                  "Hi."
//           intro:
//               "I am going to see {friend}."
//       stage go:
//           onStart:
//               moveNpc(self, friend_home, forMinutes=120)
//           complete:
//               elapsed(hours=2)
//
// Rules: a line is a property (name = value), a command (name(...)), a dialogue line (who: "text") or a block that
// ends with ":" and holds the indented lines below it. Values are JSON (strings in quotes, numbers, true/false,
// {...}, [...]); an id can be written without quotes. Names are the JSON names (see specs.js), so nothing is lost.
// Comments (#) are not kept in the JSON.

import { EFFECTS, STEPS, CONDITIONS, TRIGGERS, lookup } from './specs.js';

const IDENT = /^[A-Za-z_$][A-Za-z0-9_$.\-]*$/;
const LITERALS = { true: true, false: false, null: null };
const IN = '    ';
const ID_PROPS = new Set(['giver', 'trigger', 'npc', 'node', 'next', 'type', 'of', 'place', 'button']);
const QUEST_BLOCKS = { autoStart: 'cond', onStart: 'fx', onComplete: 'fx' };
const STAGE_BLOCKS = { onStart: 'fx', complete: 'cond', fail: 'cond', onComplete: 'fx', onFail: 'fx' };
const TEMPLATE_BLOCKS = { when: 'cond', onStart: 'fx', onComplete: 'fx', onFail: 'fx' };
const SLOT_TYPES = ['npc', 'place', 'node', 'item'];
const LINE_KEYS = new Set(['say', 'who', 'pose', 'show', 'file', 'x', 'y', 'width', 'height', 'layer', 'hide', 'sound', 'volume']);

const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const list = (v) => (v === undefined || v === null ? [] : Array.isArray(v) ? v : [v]);
const onlyKeys = (o, keys) => Object.keys(o).every((k) => keys.includes(k));

// ====================================================================== JSON -> text

/** A value as written in the script: ids and choices without quotes when they can be, everything else as JSON. */
export function valueText(v, type) {
  if (typeof v === 'string' && type && (type.startsWith('id:') || type.startsWith('choice:')) && IDENT.test(v) && !(v in LITERALS)) return v;
  return JSON.stringify(v);
}

/** name(positional..., key=value...): parameters marked pos are written without their name while none is missing. */
function callText(name, spec, obj, skip) {
  const parts = [];
  const used = new Set(skip);
  let positional = true;
  for (const p of spec.params) {
    if (!p.pos) continue;
    if (positional && obj[p.name] !== undefined) { parts.push(valueText(obj[p.name], p.type)); used.add(p.name); }
    else positional = false;
  }
  for (const [k, v] of Object.entries(obj)) {
    if (used.has(k)) continue;
    parts.push(`${k}=${valueText(v, spec.params.find((p) => p.name === k)?.type)}`);
  }
  return `${name}(${parts.join(', ')})`;
}

const rawText = (v) => `raw(${JSON.stringify(v)})`;

function effectText(fx) {
  const [name, spec] = isObj(fx) ? lookup(EFFECTS, fx.do) : [null, null];
  return spec ? callText(name, spec, fx, ['do']) : rawText(fx);
}

/** A condition on one line: playerAt(casa), any(flag(a), not(flag(b))). */
export function condText(c) {
  if (Array.isArray(c)) return `all(${c.map(condText).join(', ')})`;
  if (isObj(c)) {
    if (onlyKeys(c, ['all']) && Array.isArray(c.all)) return `all(${c.all.map(condText).join(', ')})`;
    if (onlyKeys(c, ['any']) && Array.isArray(c.any)) return `any(${c.any.map(condText).join(', ')})`;
    if (onlyKeys(c, ['not']) && c.not !== undefined) return `not(${condText(c.not)})`;
    const [name, spec] = lookup(CONDITIONS, c.type);
    if (spec) return callText(name, spec, c, ['type']);
  }
  return rawText(c);
}

/** A condition as the lines of a block: the "all" at the top becomes one line per condition, any / all / not inside become blocks. */
function condLines(c, ind, out) {
  const top = Array.isArray(c) ? c : isObj(c) && onlyKeys(c, ['all']) && Array.isArray(c.all) ? c.all : [c];
  for (const x of top) condLine(x, ind, out);
}

function condLine(c, ind, out) {
  if (isObj(c) && onlyKeys(c, ['any']) && Array.isArray(c.any)) { out.push(`${ind}any:`); for (const x of c.any) condLine(x, ind + IN, out); return; }
  if (isObj(c) && onlyKeys(c, ['all']) && Array.isArray(c.all)) { out.push(`${ind}all:`); for (const x of c.all) condLine(x, ind + IN, out); return; }
  if (isObj(c) && onlyKeys(c, ['not']) && c.not !== undefined) { out.push(`${ind}not:`); condLine(c.not, ind + IN, out); return; }
  out.push(ind + condText(c));
}

function propLines(obj, skip, ind, out) {
  for (const [k, v] of Object.entries(obj)) if (!skip.has(k)) out.push(`${ind}${k} = ${valueText(v, ID_PROPS.has(k) ? 'id:' : null)}`);
}

function block(name, kind, value, ind, out) {
  if (value === undefined || value === null) return;
  out.push(`${ind}${name}:`);
  const before = out.length;
  if (kind === 'cond') condLines(value, ind + IN, out);
  else for (const fx of list(value)) out.push(ind + IN + effectText(fx));
  if (out.length === before) out.push(`${ind + IN}pass`);
}

/** The commands of an event script, one or more lines each. */
export function stepLines(steps, ind, out) {
  const before = out.length;
  for (const s of steps) stepLine(s, ind, out);
  if (out.length === before) out.push(`${ind}pass`);
}

function isIf(s) { return isObj(s) && 'if' in s && onlyKeys(s, ['if', 'then', 'else']); }

function stepLine(s, ind, out) {
  if (!isObj(s)) { out.push(ind + rawText(s)); return; }
  if ('do' in s) { out.push(ind + effectText(s)); return; }
  if (isIf(s)) {
    let cur = s, word = 'if';
    for (;;) {
      out.push(`${ind}${word} ${condText(cur.if)}:`);
      stepLines(list(cur.then), ind + IN, out);
      const other = list(cur.else);
      if (other.length === 1 && isIf(other[0])) { cur = other[0]; word = 'elif'; continue; }
      if (other.length) { out.push(`${ind}else:`); stepLines(other, ind + IN, out); }
      return;
    }
  }
  if ('choices' in s && onlyKeys(s, ['choices']) && Array.isArray(s.choices) && s.choices.every((o) => isObj(o) && onlyKeys(o, ['text', 'when', 'then']))) {
    out.push(`${ind}choice:`);
    for (const o of s.choices) {
      out.push(`${ind + IN}option ${JSON.stringify(o.text ?? '...')}${o.when !== undefined ? ' if ' + condText(o.when) : ''}:`);
      stepLines(list(o.then), ind + IN + IN, out);
    }
    return;
  }
  if (!onlyKeys(s, [...LINE_KEYS]) || !lineIsWhole(s)) { out.push(ind + rawText(s)); return; }
  // a line step may combine commands: the game runs them in the order hide, show, sound, say
  if (s.hide !== undefined) out.push(`${ind}hide(${valueText(s.hide, 'id:')})`);
  if (s.show !== undefined) out.push(ind + callText('show', STEPS.show, pick(s, ['show', 'file', 'x', 'y', 'width', 'height', 'layer']), []));
  if (s.sound !== undefined) out.push(ind + callText('sound', STEPS.sound, pick(s, ['sound', 'volume']), []));
  if (s.say !== undefined) out.push(ind + sayText(s));
}

/** Every field of a line step belongs to a command it holds (file to show, volume to sound, who to say...). */
function lineIsWhole(s) {
  const has = (k) => s[k] !== undefined;
  if (!has('say') && !has('show') && !has('hide') && !has('sound')) return false;
  if (!has('show') && ['file', 'x', 'y', 'width', 'height', 'layer'].some(has)) return false;
  if (!has('sound') && has('volume')) return false;
  if (!has('say') && (has('who') || has('pose'))) return false;
  return true;
}

const pick = (o, keys) => Object.fromEntries(keys.filter((k) => o[k] !== undefined).map((k) => [k, o[k]]));

function sayText(s) {
  const text = JSON.stringify(s.say);
  const who = s.who;
  const pose = s.pose !== undefined && typeof s.pose === 'string' && IDENT.test(s.pose) ? ` [${s.pose}]` : null;
  if (typeof who === 'string' && (s.pose === undefined || pose)) return `${valueText(who, 'id:')}${pose || ''}: ${text}`;
  return callText('say', STEPS.say, pick(s, ['say', 'who', 'pose']), []);
}

/**
 * The commands of an event in the order they run. Older event files keep part of them elsewhere: "effects" (run first),
 * "popup" (the event text as a line) and "choices" (a choice at the end); they are moved into the list.
 */
export function eventSteps(ev) {
  const steps = list(ev.effects).slice();
  const script = Array.isArray(ev.script) ? ev.script : [];
  const choices = Array.isArray(ev.choices) ? ev.choices : [];
  if (!script.length && (ev.popup || choices.length)) steps.push(ev.npc ? { say: ev.text ?? '', who: ev.npc } : { say: ev.text ?? '' });
  steps.push(...script);
  if (choices.length) {
    steps.push({ choices: choices.map((c) => {
      const o = { text: c.text ?? '...' };
      if (c.when !== undefined) o.when = c.when;
      o.then = list(c.effects);
      return o;
    }) });
  }
  return steps;
}

// "editor" held the card positions of an older editor; the game ignores it, so it is not carried over.
function questLines(q, out) {
  out.push(`quest ${valueText(q.id, 'id:')}:`);
  propLines(q, new Set(['id', 'editor', 'stages', ...Object.keys(QUEST_BLOCKS)]), IN, out);
  for (const [name, kind] of Object.entries(QUEST_BLOCKS)) block(name, kind, q[name], IN, out);
  list(q.stages).forEach((s, i) => {
    out.push('');
    out.push(`${IN}stage ${valueText(s.id || `stage${i}`, 'id:')}:`);   // a stage without id is "stage<n>" in the game too
    const ordered = s.text !== undefined ? { text: s.text, ...s } : s;
    propLines(ordered, new Set(['id', 'next', ...Object.keys(STAGE_BLOCKS)]), IN + IN, out);
    for (const [name, kind] of Object.entries(STAGE_BLOCKS)) block(name, kind, s[name], IN + IN, out);
    if (s.next !== undefined) out.push(`${IN + IN}next = ${valueText(s.next, 'id:')}`);
  });
}

/** A text line of a group: a quoted text, or {"it": …, "en": …}. */
const isLine = (v) => typeof v === 'string' || (isObj(v) && v.lines === undefined && v.when === undefined);
const lineText = (v) => JSON.stringify(v);

/** The lines of a text group: the lines themselves (one variant that always holds) or variant blocks. */
function groupLines(variants, ind, out) {
  const list = Array.isArray(variants) ? variants : variants === undefined || variants === null ? [] : [variants];
  if (list.length && list.every(isLine)) { for (const l of list) out.push(ind + lineText(l)); return; }
  for (const v of list) {
    if (!isObj(v) || !Array.isArray(v.lines) || !v.lines.every(isLine) || !onlyKeys(v, ['when', 'lines'])) { out.push(`${ind}variant:`); out.push(`${ind + IN}${rawText(v)}`); continue; }
    out.push(`${ind}variant${v.when !== undefined ? ' if ' + condText(v.when) : ''}:`);
    for (const l of v.lines) out.push(ind + IN + lineText(l));
    if (!v.lines.length) out.push(`${ind + IN}pass`);
  }
}

function templateLines(t, out) {
  out.push(`template ${valueText(t.id, 'id:')}:`);
  propLines(t, new Set(['id', 'weight', 'slots', 'texts', 'stages', ...Object.keys(TEMPLATE_BLOCKS)]), IN, out);
  block('when', 'cond', t.when, IN, out);
  if (isObj(t.weight)) { out.push(`${IN}weight:`); propLines(t.weight, new Set(), IN + IN, out); if (!Object.keys(t.weight).length) out.push(`${IN + IN}pass`); }
  for (const sl of list(t.slots)) {
    if (!isObj(sl)) continue;
    out.push(`${IN}slot ${valueText(sl.id ?? 'slot', 'id:')}:`);
    propLines(sl, new Set(['id', 'when']), IN + IN, out);
    block('when', 'cond', sl.when, IN + IN, out);
    if (Object.keys(sl).every((k) => k === 'id')) out.push(`${IN + IN}pass`);
  }
  if (isObj(t.texts)) {
    out.push(`${IN}texts:`);
    for (const [name, variants] of Object.entries(t.texts)) { out.push(`${IN + IN}${name}:`); groupLines(variants, IN + IN + IN, out); }
    if (!Object.keys(t.texts).length) out.push(`${IN + IN}pass`);
  }
  for (const name of ['onStart', 'onComplete', 'onFail']) block(name, 'fx', t[name], IN, out);
  list(t.stages).forEach((st, i) => {
    out.push('');
    out.push(`${IN}stage ${valueText(st.id || `stage${i}`, 'id:')}:`);
    const ordered = st.text !== undefined ? { text: st.text, ...st } : st;
    propLines(ordered, new Set(['id', 'next', ...Object.keys(STAGE_BLOCKS)]), IN + IN, out);
    for (const [name, kind] of Object.entries(STAGE_BLOCKS)) block(name, kind, st[name], IN + IN, out);
    if (st.next !== undefined) out.push(`${IN + IN}next = ${valueText(st.next, 'id:')}`);
  });
}

function linesLines(l, out) {
  out.push(`lines ${valueText(l.id, 'id:')}:`);
  propLines(l, new Set(['id', 'variants']), IN, out);
  groupLines(l.variants, IN, out);
}

function eventLines(ev, out) {
  out.push(`event ${valueText(ev.id, 'id:')}:`);
  propLines(ev, new Set(['id', 'editor', 'when', 'script', 'effects', 'choices', 'popup']), IN, out);
  block('when', 'cond', ev.when, IN, out);
  const steps = eventSteps(ev);
  if (steps.length) { out.push(''); stepLines(steps, IN, out); }
}

/** Text of a set of quests and events (units: [{ kind: 'quest' | 'event', json }]). */
export function toScript(units) {
  const out = [];
  for (const u of units) {
    if (out.length) out.push('');
    if (u.kind === 'quest') questLines(u.json, out);
    else if (u.kind === 'template') templateLines(u.json, out);
    else if (u.kind === 'lines') linesLines(u.json, out);
    else eventLines(u.json, out);
  }
  return out.join('\n') + '\n';
}

// ====================================================================== text -> JSON

class LineError extends Error {}

/** The lines that hold something, without comments: [{ no, indent, src }]. */
function splitLines(text) {
  const out = [];
  text.split(/\r?\n/).forEach((raw, i) => {
    let src = '', quote = null;
    for (let j = 0; j < raw.length; j++) {
      const c = raw[j];
      if (quote) { src += c; if (c === '\\') { src += raw[++j] ?? ''; } else if (c === quote) quote = null; continue; }
      if (c === '#') break;
      if (c === '"' || c === "'") quote = c;
      src += c;
    }
    if (!src.trim()) return;
    let indent = 0, k = 0;
    for (; k < src.length && (src[k] === ' ' || src[k] === '\t'); k++) indent += src[k] === '\t' ? 4 : 1;
    out.push({ no: i + 1, indent, src: src.slice(k).trimEnd() });
  });
  return out;
}

/** Nests the lines by indentation, like Python: a line ending with ":" opens a block. */
function buildTree(lines, errors) {
  const roots = [];
  const stack = [{ indent: -1, kids: roots, opens: true }];
  for (const l of lines) {
    while (stack[stack.length - 1].indent >= l.indent) stack.pop();
    const parent = stack[stack.length - 1];
    if (!parent.opens) errors.push({ line: l.no, message: 'unexpected indent: the line above does not open a block (it does not end with ":")' });
    else if (parent.kids.length && parent.kids[0].indent !== l.indent) errors.push({ line: l.no, message: 'indent not aligned with the lines of the same block' });
    const node = { ...l, kids: [], opens: l.src.endsWith(':') };
    parent.kids.push(node);
    stack.push(node);
  }
  return roots;
}

function tokenize(src) {
  const toks = [];
  let i = 0;
  while (i < src.length) {
    const c = src[i];
    if (c === ' ' || c === '\t') { i++; continue; }
    if (c === '"' || c === "'") {
      let j = i + 1;
      while (j < src.length && src[j] !== c) j += src[j] === '\\' ? 2 : 1;
      if (j >= src.length) throw new LineError('quoted text not closed');
      let body = src.slice(i + 1, j);
      if (c === "'") body = body.replace(/\\'/g, "'").replace(/"/g, '\\"');
      try { toks.push({ t: 'str', v: JSON.parse(`"${body}"`) }); } catch { throw new LineError('invalid quoted text'); }
      i = j + 1;
      continue;
    }
    const rest = src.slice(i);
    let m;
    if ((m = /^\d{1,2}:\d{2}(?!\d)/.exec(rest))) { toks.push({ t: 'str', v: m[0].padStart(5, '0') }); i += m[0].length; continue; }
    if ((m = /^-?\d+(\.\d+)?(?![A-Za-z_])/.exec(rest))) { toks.push({ t: 'num', v: Number(m[0]) }); i += m[0].length; continue; }
    if ((m = /^[A-Za-z_$][A-Za-z0-9_$.\-]*/.exec(rest))) {
      toks.push(m[0] in LITERALS ? { t: 'lit', v: LITERALS[m[0]] } : { t: 'id', v: m[0] });
      i += m[0].length;
      continue;
    }
    if ('()[]{},=:'.includes(c)) { toks.push({ t: c }); i++; continue; }
    throw new LineError(`unexpected character “${c}”`);
  }
  return toks;
}

/** A call in the text: name(args, key=value). */
class Call { constructor(name, args, kwargs) { this.name = name; this.args = args; this.kwargs = kwargs; } }

class Tokens {
  constructor(toks) { this.toks = toks; this.i = 0; }
  peek(k = 0) { return this.toks[this.i + k]; }
  next() { return this.toks[this.i++]; }
  done() { return this.i >= this.toks.length; }
  is(t, k = 0) { return this.peek(k)?.t === t; }
  expect(t) { if (!this.is(t)) throw new LineError(`expected “${t}”`); return this.next(); }
  end() { if (!this.done()) throw new LineError('there is something extra at the end of the line'); }

  value() {
    const tk = this.next();
    if (!tk) throw new LineError('a value is missing');
    if (tk.t === 'str' || tk.t === 'num' || tk.t === 'lit') return tk.v;
    if (tk.t === 'id') return this.is('(') ? this.call(tk.v) : tk.v;
    if (tk.t === '{') {
      const o = {};
      while (!this.is('}')) {
        const key = this.next();
        if (!key || (key.t !== 'str' && key.t !== 'id')) throw new LineError('field name expected');
        this.expect(':');
        o[key.v] = plain(this.value());
        if (!this.is('}')) this.expect(',');
      }
      this.next();
      return o;
    }
    if (tk.t === '[') {
      const a = [];
      while (!this.is(']')) { a.push(plain(this.value())); if (!this.is(']')) this.expect(','); }
      this.next();
      return a;
    }
    throw new LineError(`“${tk.t}” is not a value`);
  }

  call(name) {
    this.expect('(');
    const args = [], kwargs = [];
    while (!this.is(')')) {
      if (this.done()) throw new LineError(`${name}(: the closing parenthesis “)” is missing`);
      if (this.is('id') && this.is('=', 1)) { const k = this.next().v; this.next(); kwargs.push([k, this.value()]); }
      else { if (kwargs.length) throw new LineError('values without a name go before the named ones'); args.push(this.value()); }
      if (this.done()) throw new LineError(`${name}(: the closing parenthesis “)” is missing`);
      if (!this.is(')') && !this.is(',')) throw new LineError(`${name}(...): two values need a comma between them`);
      if (this.is(',')) this.next();
    }
    this.next();
    return new Call(name, args, kwargs);
  }
}

function plain(v) {
  if (v instanceof Call) throw new LineError(`${v.name}(...) cannot be inside a value`);
  return v;
}

/** What a line is: { kind: 'prop' | 'block' | 'say' | 'call', ... }. */
function classify(node) {
  const tk = new Tokens(tokenize(node.src));
  if (tk.is('id') && tk.is('=', 1)) {
    const name = tk.next().v; tk.next();
    const value = plain(tk.value()); tk.end();
    return { kind: 'prop', name, value };
  }
  if (node.opens) {
    tk.toks = tk.toks.slice(0, -1);
    if (!tk.is('id')) throw new LineError('a block starts with a name (quest, event, stage, when, if, choice, option…)');
    return { kind: 'block', word: tk.next().v, tk };
  }
  if ((tk.is('id') || tk.is('str')) && (tk.is(':', 1) || (tk.is('[', 1) && tk.is('id', 2) && tk.is(']', 3) && tk.is(':', 4)))) {
    const who = tk.next().v;
    let pose;
    if (tk.is('[')) { tk.next(); pose = tk.next().v; tk.next(); }
    tk.next();
    const text = plain(tk.value()); tk.end();
    return { kind: 'say', who, pose, text };
  }
  if (tk.is('id') && tk.toks.length === 1) return { kind: 'call', call: new Call(tk.next().v, [], []) };
  if ((tk.is('id') || tk.is('str')) && tk.is('str', 1)) throw new LineError(`a dialogue line needs the colon: ${valueText(tk.peek().v, 'id:')}: "text"`);
  const v = tk.value(); tk.end();
  if (!(v instanceof Call)) throw new LineError('line not recognized: it must be a command like giveItem(bread, 1), a dialogue line like elena: "Hello", a property like title = "…" or a block that ends with ":"');
  return { kind: 'call', call: v };
}

/** Builds the JSON object of a call from its spec: positional values fill the parameters marked pos, in order. */
function callObject(call, spec, base, warn, check) {
  const obj = { ...base };
  const posParams = spec.params.filter((p) => p.pos);
  call.args.forEach((v, i) => {
    if (i >= posParams.length) throw new LineError(`${call.name}: too many values without a name (at most ${posParams.length})`);
    obj[posParams[i].name] = plain(v);
  });
  for (const [k, v] of call.kwargs) obj[k] = plain(v);
  for (const p of spec.params) {
    if (p.req && obj[p.name] === undefined) warn(`${call.name}: “${p.name}” is missing (${p.label})`);
    else if (check && obj[p.name] !== undefined) check(p.type, obj[p.name], warn, obj);
  }
  return obj;
}

export class Parser {
  /** refs: the ids of the world (store.js refs); with them, an id that does not exist becomes a warning on its line. */
  constructor(refs) {
    this.errors = []; this.warnings = [];
    this.refs = refs || null;
    this.check = refs ? (type, value, warn, obj) => this.checkId(type, value, warn, obj) : null;
  }

  checkId(type, value, warn, obj = {}) {
    if (typeof value !== 'string' || !type) return;
    const r = this.refs;
    const has = (arr) => (arr || []).includes(value);
    const [kind, sub] = type.split(':');
    let ok = true, what = '';
    // in an automatic quest: self, it and the slots stand for its own characters, places and items
    if (kind === 'id' && this.tpl && ['npc', 'target', 'node', 'item'].includes(sub)) {
      const want = sub === 'node' ? ['place', 'node'] : sub === 'item' ? ['item'] : ['npc'];
      if (value === 'self' && want[0] === 'npc') return;
      if (value === 'it' && this.tpl.it) { if (!want.includes(this.tpl.it)) warn(`“it” is a ${this.tpl.it} slot here, not a ${want[0]}`); return; }
      if (this.tpl.slots.has(value)) { const t = this.tpl.slots.get(value); if (!want.includes(t)) warn(`the slot “${value}” is a ${t}, not a ${want[0]}`); return; }
    }
    if (kind === 'file') { ok = has(r[sub]); what = `file in ${sub}/`; }
    else if (kind === 'id') {
      switch (sub) {
        case 'npc': ok = has(r.npc) || value === 'player'; what = 'character'; break;
        case 'target': ok = value === 'player' || has(r.npc); what = 'character'; break;
        case 'node': ok = has(r.node); what = 'place'; break;
        case 'item': ok = has(r.item); what = 'item'; break;
        case 'quest': ok = has(r.quest) || this.known.quests.has(value); what = 'quest'; break;
        case 'link': ok = has(r.link); what = 'link'; break;
        case 'stat': ok = has(r.stat); what = 'stat'; break;
        case 'activity': ok = has(r.activity); what = 'activity'; break;
        case 'button': ok = has(r.button); what = 'button of the bar (hud.json)'; break;
        case 'goal': ok = has(r.goal) || value === 'none'; what = 'goal (rules.json)'; break;
        case 'template': ok = has(r.template) || this.known.templates.has(value); what = 'automatic quest'; break;
        case 'lines': ok = has(r.lines) || this.known.lines.has(value) || !!this.tpl?.groups?.has(value); what = 'lines (a text group of the automatic quest or a file in lines/)'; break;
        case 'tstage': {
          const t = obj.template;
          const stages = new Set([...(r.tstage?.[t] || []), ...(this.known.tstages.get(t) || [])]);
          ok = !t || stages.has(value); what = `stage of ${t}`; break;
        }
        case 'stage': {
          const q = obj.quest || this.curQuest;
          const stages = new Set([...(r.stage?.[q] || []), ...(this.known.stages.get(q) || [])]);
          ok = !q || stages.has(value) || value === 'end'; what = `stage of ${q}`; break;
        }
        default: return;
      }
    } else return;
    if (!ok) warn(`“${value}” does not exist (${what})`);
  }

  /** Ids the script itself defines (a new quest is not in the world's files until it is saved). */
  scanKnown(roots) {
    this.known = { quests: new Set(), stages: new Map(), templates: new Set(), tstages: new Map(), lines: new Set() };
    const stagesOf = (n) => n.kids.map((k) => /^stage\s+"?([^\s":]+)"?\s*:$/.exec(k.src)?.[1]).filter(Boolean);
    for (const n of roots) {
      const m = /^(quest|template|lines)\s+"?([^\s":]+)"?\s*:$/.exec(n.src);
      if (!m) continue;
      if (m[1] === 'quest') { this.known.quests.add(m[2]); this.known.stages.set(m[2], stagesOf(n)); }
      else if (m[1] === 'template') { this.known.templates.add(m[2]); this.known.tstages.set(m[2], stagesOf(n)); }
      else this.known.lines.add(m[2]);
    }
  }

  checkProp(name, value, warn) {
    if (!this.check) return;
    if (name === 'trigger' && !TRIGGERS.includes(value)) warn(`trigger “${value}” does not exist: ${TRIGGERS.slice(0, -1).join(', ')} or ${TRIGGERS[TRIGGERS.length - 1]}`);
    else if (name === 'npc' || name === 'giver') this.checkId('id:npc', value, warn);
    else if (name === 'button') this.checkId('id:button', value, warn);
    else if (name === 'node') this.checkId('id:node', value, warn);
    else if (name === 'next' && !this.tpl) this.checkId('id:stage', value, warn);
  }

  error(line, message) { this.errors.push({ line, message }); }

  /** Runs fn on a line; a LineError becomes an error of that line and the line is skipped. */
  at(node, fn) {
    try { return fn((m) => this.warnings.push({ line: node.no, message: m })); }
    catch (e) { if (e instanceof LineError) { this.error(node.no, e.message); return undefined; } throw e; }
  }

  cond(v, warn) {
    if (!(v instanceof Call)) throw new LineError('a condition goes here, for example playerAt(home) or flag(key, ">=", 1)');
    const name = v.name;
    if (name === 'all' || name === 'any') return { [name]: v.args.map((a) => this.cond(a, warn)) };
    if (name === 'not') { if (v.args.length !== 1) throw new LineError('not(...) takes a single condition'); return { not: this.cond(v.args[0], warn) }; }
    if (name === 'raw') return this.raw(v);
    const [canon, spec] = lookup(CONDITIONS, name);
    if (spec) return callObject(v, spec, { type: canon }, warn, this.check);
    warn(`unknown condition “${name}”: the game will refuse it`);
    return callObject(v, { params: [] }, { type: name }, warn);
  }

  raw(call) {
    if (call.args.length !== 1 || !isObj(call.args[0]) || call.kwargs.length) throw new LineError('raw(...) takes a single JSON object');
    return call.args[0];
  }

  /** The lines of a condition block: one condition each (all of them must hold), any: / all: / not: open groups. */
  condBlock(nodes) {
    const out = [];
    for (const n of nodes) {
      const c = this.at(n, (warn) => {
        const line = classify(n);
        if (line.kind === 'block' && ['any', 'all', 'not'].includes(line.word)) {
          line.tk.end();
          const kids = this.condBlock(n.kids);
          if (line.word === 'not') { if (kids.length !== 1) throw new LineError('not: takes a single condition below'); return { not: kids[0] }; }
          return { [line.word]: kids };
        }
        if (line.kind === 'call') return this.cond(line.call, warn);
        throw new LineError('conditions go here, one per line');
      });
      if (c !== undefined) out.push(c);
    }
    return out;
  }

  condValue(node) {
    const items = this.condBlock(node.kids);
    if (!items.length) { this.error(node.no, 'the block has no conditions'); return undefined; }
    return items.length === 1 ? items[0] : { all: items };
  }

  /** The commands of a block. inEvent: lines, images, if and choice are allowed (an event script); else only effects. */
  commands(nodes, inEvent) {
    const out = [];
    for (let i = 0; i < nodes.length; i++) {
      const n = nodes[i];
      const step = this.at(n, (warn) => {
        const line = classify(n);
        if (line.kind === 'call' && line.call.name === 'pass' && !line.call.args.length) return null;
        if (line.kind === 'prop') throw new LineError(`the property “${line.name}” goes at the top, before the commands`);
        if (!inEvent && (line.kind === 'say' || line.kind === 'block')) throw new LineError('only game commands go here: dialogue lines, choices and “if” belong in messages (event)');
        if (line.kind === 'say') {
          const s = { say: line.text };
          if (line.who !== undefined && line.who !== '') s.who = line.who;
          if (line.pose !== undefined) s.pose = line.pose;
          return s;
        }
        if (line.kind === 'block') {
          if (line.word === 'if') {
            const s = { if: this.cond(line.tk.value(), warn), then: this.commands(n.kids, true) };
            line.tk.end();
            let tail = s;
            while (i + 1 < nodes.length && /^(elif|else)\b/.test(nodes[i + 1].src) && nodes[i + 1].opens) {
              const m = nodes[++i];
              const ml = this.at(m, () => classify(m));
              if (!ml) break;
              if (ml.word === 'else') { ml.tk.end(); tail.else = this.commands(m.kids, true); break; }
              const inner = this.at(m, (w) => { const c = this.cond(ml.tk.value(), w); ml.tk.end(); return { if: c, then: this.commands(m.kids, true) }; });
              if (!inner) break;
              tail.else = [inner];
              tail = inner;
            }
            return s;
          }
          if (line.word === 'elif' || line.word === 'else') throw new LineError(`“${line.word}” without an “if” before it`);
          if (line.word === 'choice') { line.tk.end(); return { choices: this.options(n) }; }
          throw new LineError(`block “${line.word}” is not allowed here`);
        }
        return this.command(line.call, inEvent, warn);
      });
      if (step) out.push(step);
    }
    return out;
  }

  options(node) {
    const out = [];
    for (const n of node.kids) {
      const o = this.at(n, (warn) => {
        const line = classify(n);
        if (line.kind !== 'block' || line.word !== 'option') throw new LineError('inside “choice:” go lines like  option "Text":');
        const opt = { text: plain(line.tk.value()) };
        if (line.tk.is('id') && line.tk.peek().v === 'if') { line.tk.next(); opt.when = this.cond(line.tk.value(), warn); }
        line.tk.end();
        opt.then = this.commands(n.kids, true);
        return opt;
      });
      if (o) out.push(o);
    }
    if (!out.length) this.error(node.no, 'a choice needs at least one “option”');
    return out;
  }

  command(call, inEvent, warn) {
    if (call.name === 'raw') return this.raw(call);
    const [stepName, stepSpec] = lookup(STEPS, call.name);
    if (stepSpec) {
      if (!inEvent) throw new LineError(`${stepName}(...) is only used in messages (event); use the game commands here`);
      return callObject(call, stepSpec, {}, warn, this.check);
    }
    const [canon, spec] = lookup(EFFECTS, call.name);
    if (spec) return callObject(call, spec, { do: canon }, warn, this.check);
    if (lookup(CONDITIONS, call.name)[1]) throw new LineError(`${call.name}(...) is a condition: it goes in a when: block, in an if or in an option … if`);
    warn(`unknown command “${call.name}”: the game will refuse it`);
    return callObject(call, { params: [] }, { do: call.name }, warn);
  }

  quest(node, id) {
    const q = { id };
    this.curQuest = id;
    const blocks = {}, stages = [];
    for (const n of node.kids) {
      this.at(n, (warn) => {
        const line = classify(n);
        if (line.kind === 'prop') { if (line.name === 'id') throw new LineError('the id is written after “quest”'); q[line.name] = line.value; this.checkProp(line.name, line.value, warn); return; }
        if (line.kind !== 'block') throw new LineError('a quest holds properties (title = …), blocks (autoStart:, onStart:, onComplete:) and stages (stage …:)');
        if (line.word === 'stage') { const sid = line.tk.value(); line.tk.end(); stages.push(this.stage(n, plain(sid))); return; }
        if (!(line.word in QUEST_BLOCKS)) throw new LineError(`block “${line.word}” is not allowed in a quest`);
        line.tk.end();
        blocks[line.word] = QUEST_BLOCKS[line.word] === 'cond' ? this.condValue(n) : this.commands(n.kids, false);
      });
    }
    for (const k of Object.keys(QUEST_BLOCKS)) if (blocks[k] !== undefined) q[k] = blocks[k];
    q.stages = stages;
    if (!stages.length) this.error(node.no, `the quest “${id}” has no stages (stage …:)`);
    if (q.busyGiver !== undefined && typeof q.busyGiver !== 'boolean') this.warnings.push({ line: node.no, message: 'busyGiver is true or false' });
    else if (q.busyGiver && !q.giver) this.error(node.no, `the quest “${id}” has busyGiver = true but no giver: who should be busy?`);
    return q;
  }

  stage(node, id) {
    const s = { id };
    const blocks = {};
    for (const n of node.kids) {
      this.at(n, (warn) => {
        const line = classify(n);
        if (line.kind === 'prop') { if (line.name === 'id') throw new LineError('the id is written after “stage”'); s[line.name] = line.value; this.checkProp(line.name, line.value, warn); return; }
        if (line.kind !== 'block' || !(line.word in STAGE_BLOCKS)) throw new LineError('a stage holds properties (text = …, next = …) and the blocks complete:, fail:, onComplete:, onFail:');
        line.tk.end();
        blocks[line.word] = STAGE_BLOCKS[line.word] === 'cond' ? this.condValue(n) : this.commands(n.kids, false);
      });
    }
    const next = s.next;
    delete s.next;
    for (const k of Object.keys(STAGE_BLOCKS)) if (blocks[k] !== undefined) s[k] = blocks[k];
    if (next !== undefined) s.next = next;
    return s;
  }

  /** A line of a text group: "text" or {"it": …, "en": …}. */
  textLine(node) {
    return this.at(node, () => {
      const tk = new Tokens(tokenize(node.src));
      if (tk.is('id') && tk.toks.length === 1 && tk.peek().v === 'pass') return null;
      const v = plain(tk.value());
      tk.end();
      if (!isLine(v)) throw new LineError('a line of text goes here: "text" (or {"it": "…", "en": "…"})');
      return v;
    }) ?? undefined;
  }

  /** The kids of a text group: lines (one variant that always holds) or blocks  variant if cond:  /  variant:  with lines inside. */
  textGroup(node) {
    const kids = node.kids;
    if (!kids.length) { this.error(node.no, 'the group has no lines'); return []; }
    if (kids.every((k) => !k.opens)) {
      const lines = kids.map((k) => this.textLine(k)).filter((v) => v !== undefined && v !== null);
      return lines;
    }
    const out = [];
    for (const n of kids) {
      const v = this.at(n, (warn) => {
        const line = classify(n);
        if (line.kind !== 'block' || line.word !== 'variant') throw new LineError('a group holds lines of text, or blocks  variant if <condition>:  and  variant:');
        const variant = {};
        if (line.tk.is('id') && line.tk.peek().v === 'if') { line.tk.next(); variant.when = this.cond(line.tk.value(), warn); }
        line.tk.end();
        variant.lines = n.kids.map((k) => this.textLine(k)).filter((x) => x !== undefined && x !== null);
        if (!variant.lines.length) throw new LineError('the variant has no lines');
        return variant;
      });
      if (v) out.push(v);
    }
    return out;
  }

  template(node, id) {
    const t = { id };
    this.curQuest = null;
    // the names of the slots first: every part of the template can use them
    this.tpl = { slots: new Map(), groups: new Set(), it: null };
    for (const n of node.kids) {
      const m = /^slot\s+"?([^\s":]+)"?\s*:$/.exec(n.src);
      if (!m) continue;
      const typeLine = n.kids.map((k) => /^type\s*=\s*"?(\w+)"?\s*$/.exec(k.src)?.[1]).find(Boolean) || 'npc';
      this.tpl.slots.set(m[1], typeLine);
    }
    for (const n of node.kids) {
      if (/^texts\s*:$/.test(n.src)) for (const g of n.kids) { const gm = /^([A-Za-z_][\w\-]*)\s*:$/.exec(g.src); if (gm) this.tpl.groups.add(gm[1]); }
    }
    const blocks = {}, stages = [], slots = [];
    let weight, texts;
    for (const n of node.kids) {
      this.at(n, (warn) => {
        const line = classify(n);
        if (line.kind === 'prop') { if (line.name === 'id') throw new LineError('the id is written after “template”'); t[line.name] = line.value; return; }
        if (line.kind !== 'block') throw new LineError('an automatic quest holds properties (title = …), blocks (when:, weight:, texts:, onStart:, onComplete:, onFail:), slots (slot …:) and stages (stage …:)');
        if (line.word === 'stage') { const sid = line.tk.value(); line.tk.end(); stages.push(this.stage(n, plain(sid))); return; }
        if (line.word === 'slot') { const sid = line.tk.value(); line.tk.end(); slots.push(this.slot(n, plain(sid))); return; }
        if (line.word === 'weight') {
          line.tk.end();
          weight = {};
          for (const k of n.kids) this.at(k, () => {
            const l = classify(k);
            if (l.kind === 'call' && l.call.name === 'pass') return;
            if (l.kind !== 'prop') throw new LineError('weight: holds  base = 10,  stats = {"stat": 0.5},  goals = {"goal": 30},  relations = {"slot": 0.2}');
            weight[l.name] = l.value;
          });
          return;
        }
        if (line.word === 'texts') {
          line.tk.end();
          texts = {};
          for (const g of n.kids) this.at(g, () => {
            const l = classify(g);
            if (l.kind === 'call' && l.call.name === 'pass') return;
            if (l.kind !== 'block') throw new LineError('texts: holds groups, for example  intro:  with the lines below');
            l.tk.end();
            texts[l.word] = this.textGroup(g);
          });
          return;
        }
        if (!(line.word in TEMPLATE_BLOCKS)) throw new LineError(`block “${line.word}” is not allowed in an automatic quest`);
        line.tk.end();
        blocks[line.word] = TEMPLATE_BLOCKS[line.word] === 'cond' ? this.condValue(n) : this.commands(n.kids, false);
      });
    }
    if (blocks.when !== undefined) t.when = blocks.when;
    if (weight !== undefined) t.weight = weight;
    if (slots.length) t.slots = slots;
    if (texts !== undefined) t.texts = texts;
    for (const k of ['onStart', 'onComplete', 'onFail']) if (blocks[k] !== undefined) t[k] = blocks[k];
    t.stages = stages;
    if (!stages.length) this.error(node.no, `the automatic quest “${id}” has no stages (stage …:)`);
    this.tpl = null;
    return t;
  }

  slot(node, id) {
    const sl = { id };
    let when;
    for (const n of node.kids) {
      this.at(n, (warn) => {
        const line = classify(n);
        if (line.kind === 'call' && line.call.name === 'pass') return;
        if (line.kind === 'prop') {
          if (line.name === 'id') throw new LineError('the id is written after “slot”');
          sl[line.name] = line.value;
          if (line.name === 'type' && !SLOT_TYPES.includes(line.value)) warn(`slot type “${line.value}” does not exist: ${SLOT_TYPES.join(', ')}`);
          if (line.name === 'nodes' && Array.isArray(line.value)) for (const v of line.value) this.check?.('id:node', v, warn);
          if (line.name === 'items' && Array.isArray(line.value)) for (const v of line.value) this.check?.('id:item', v, warn);
          return;
        }
        if (line.kind !== 'block' || line.word !== 'when') throw new LineError('a slot holds properties (type = npc | place | node | item, free = true, of = …, place = …, nodes = […], items = […]) and a when: block');
        line.tk.end();
        this.tpl.it = sl.type || 'npc';
        when = this.condValue(n);
        this.tpl.it = null;
      });
    }
    if (when !== undefined) sl.when = when;
    return sl;
  }

  lines(node, id) {
    const l = { id };
    this.curQuest = null;
    this.tpl = { slots: new Map(), groups: new Set(), it: null };      // the shared lines know only self
    const props = node.kids.filter((k) => /^[A-Za-z_]\w*\s*=/.test(k.src));
    for (const n of props) this.at(n, () => { const line = classify(n); if (line.name === 'id') throw new LineError('the id is written after “lines”'); l[line.name] = line.value; });
    l.variants = this.textGroup({ ...node, kids: node.kids.filter((k) => !props.includes(k)) });
    this.tpl = null;
    return l;
  }

  event(node, id) {
    const ev = { id };
    this.curQuest = null;
    let when;
    const cmds = [];
    for (const n of node.kids) {
      const head = this.at(n, () => classify(n));
      if (!head) continue;
      if (head.kind === 'prop') {
        if (head.name === 'id') this.error(n.no, 'the id is written after “event”');
        else { ev[head.name] = head.value; this.at(n, (warn) => this.checkProp(head.name, head.value, warn)); }
        continue;
      }
      if (head.kind === 'block' && head.word === 'when') { this.at(n, () => head.tk.end()); when = this.condValue(n); continue; }
      cmds.push(n);
    }
    if (when !== undefined) ev.when = when;
    const script = this.commands(cmds, true);
    if (script.length) ev.script = script;
    if (ev.trigger === undefined) this.error(node.no, `the message “${id}” does not say when it starts: add trigger = ${TRIGGERS.join(' | ')}`);
    if (ev.trigger === 'button' && ev.button === undefined) this.error(node.no, `the message “${id}” starts from a button of the bar: it needs button = <id of a hud.json button>`);
    if (ev.trigger === 'talk' && ev.npc === undefined) this.error(node.no, `the message “${id}” starts by talking: it needs npc = <character>`);
    return ev;
  }

  units(text) {
    const lines = splitLines(text);
    const roots = buildTree(lines, this.errors);
    this.scanKnown(roots);
    const units = [];
    const seen = new Set();
    for (const n of roots) {
      const u = this.at(n, () => {
        const line = classify(n);
        if (line.kind !== 'block' || !['quest', 'event', 'template', 'lines'].includes(line.word)) throw new LineError('only “quest id:”, “event id:”, “template id:” or “lines id:” blocks go at the top of the file');
        const id = plain(line.tk.value());
        line.tk.end();
        if (typeof id !== 'string' || !id) throw new LineError('an id is needed');
        const key = `${line.word}:${id}`;
        if (seen.has(key)) throw new LineError(`“${id}” is already defined above`);
        seen.add(key);
        const json = line.word === 'quest' ? this.quest(n, id) : line.word === 'template' ? this.template(n, id) : line.word === 'lines' ? this.lines(n, id) : this.event(n, id);
        return { kind: line.word, line: n.no, json };
      });
      if (u) units.push(u);
    }
    return units;
  }
}

/** Reads a script: { units: [{ kind, line, json }], errors: [{ line, message }], warnings: [...] }. With refs, unknown ids are warnings. */
export function fromScript(text, refs) {
  const p = new Parser(refs);
  const units = p.units(text);
  return { units, errors: p.errors, warnings: p.warnings };
}
