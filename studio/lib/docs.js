// Documents: what the program opens and saves as one script.
//  - a quest: quests/<id>.json and the events in events/<id>/ (the messages of that quest);
//  - a folder of events that is not a quest's: events/<folder>/;
//  - one event file directly in events/;
//  - an automatic quest: templates/<id>.json;
//  - the shared lines: every file of lines/.
// The JSON files stay the only truth: a document is read from them and written back to them.

import { toScript, fromScript } from './script.js';

/** JSON as the game's files are written: two spaces, LF, a final newline. */
export const jsonText = (o) => JSON.stringify(o, null, 2) + '\n';

/**
 * The units of a document from its files ([{ rel, json }]): the quests first, then the events in the order the story
 * goes: those that start one of the quests, then by the first stage their condition asks for, then the others; ties by file name.
 */
export function docUnits(files) {
  const sorted = [...files].sort((a, b) => a.rel.localeCompare(b.rel));
  const quests = sorted.filter((f) => f.rel.startsWith('quests/')).map((f) => f.json);
  const stageIndex = new Map();
  for (const q of quests) (q.stages || []).forEach((s, i) => stageIndex.set(`${q.id}/${s.id || `stage${i}`}`, i));
  const rank = (ev) => {
    let best = Infinity, starts = false;
    const scan = (v) => {
      if (Array.isArray(v)) return v.forEach(scan);
      if (!v || typeof v !== 'object') return;
      if (v.type === 'quest' && stageIndex.has(`${v.quest}/${v.stage}`)) best = Math.min(best, stageIndex.get(`${v.quest}/${v.stage}`));
      if (v.do === 'startQuest' && quests.some((q) => q.id === v.quest)) starts = true;
      Object.values(v).forEach(scan);
    };
    scan(ev.when);
    scan([ev.script, ev.effects, ev.choices]);
    return starts && best === Infinity ? -1 : best;
  };
  const events = sorted.filter((f) => f.rel.startsWith('events/')).map((f, i) => ({ json: f.json, r: rank(f.json), i }));
  events.sort((a, b) => a.r - b.r || a.i - b.i);
  const templates = sorted.filter((f) => f.rel.startsWith('templates/')).map((f) => ({ kind: 'template', json: f.json }));
  const lines = sorted.filter((f) => f.rel.startsWith('lines/')).map((f) => ({ kind: 'lines', json: f.json }));
  return [...quests.map((json) => ({ kind: 'quest', json })), ...templates, ...lines, ...events.map((e) => ({ kind: 'event', json: e.json }))];
}

export const docScript = (files) => toScript(docUnits(files));

/**
 * The files a script writes: quests go to quests/<id>.json, automatic quests to templates/<id>.json, shared lines to
 * lines/<id>.json, events to <doc.eventDir>/<id>.json. Files the document had
 * that the script no longer produces (an event removed or renamed) are listed in remove.
 * Returns { write: { rel: text }, remove: [rel], errors, warnings } and writes nothing when there are errors.
 */
export function docFiles(script, doc, refs) {
  const { units, errors, warnings } = fromScript(script, refs);
  const write = {};
  for (const u of units) {
    const rel = u.kind === 'quest' ? `quests/${u.json.id}.json` : u.kind === 'template' ? `templates/${u.json.id}.json` : u.kind === 'lines' ? `lines/${u.json.id}.json` : `${doc.eventDir}/${u.json.id}.json`;
    if (!/^[A-Za-z0-9_.\-]+$/.test(u.json.id)) errors.push({ line: u.line, message: `“${u.json.id}”: an id can only have letters, digits, _ - . (it becomes the file name)` });
    else if (write[rel]) errors.push({ line: u.line, message: `“${u.json.id}” is there twice` });
    else write[rel] = jsonText(u.json);
  }
  if (errors.length) return { write: {}, remove: [], errors, warnings, units };
  const remove = (doc.files || []).filter((rel) => !(rel in write));
  return { write, remove, errors, warnings, units };
}
