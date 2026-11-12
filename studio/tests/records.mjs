// Checks of lib/records.js: routines resolved as the game does (NpcLogic.Resolve), days and times, the effects of the
// sleep written as script lines and back, where an id is used, and the checks run on every world and test pack.
//   node tests/records.mjs

import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { listRecords, refs as packRefs, allJson } from '../lib/store.js';
import { whereAt, routineBlocks, dayMask, daysOf, parseTime, checkRecord, effectsToText, effectsFromText, usages, condToText, condFromText } from '../lib/records.js';

const here = dirname(fileURLToPath(import.meta.url));
let failed = 0, passed = 0;
const eq = (name, got, want) => {
  if (JSON.stringify(got) === JSON.stringify(want)) { passed++; return; }
  failed++;
  console.log(`FAILED ${name}\n  expected: ${JSON.stringify(want)}\n  got:      ${JSON.stringify(got)}`);
};

// ---------------------------------------------------------------- days and times
eq('days all', dayMask('all'), 0x7f);
eq('days missing', dayMask(undefined), 0x7f);
eq('days weekdays', dayMask('weekdays'), 0x1f);
eq('days list', dayMask(['mon', 'wed']), 0b101);
eq('days unknown', dayMask('lunedi'), -1);
eq('days back', [0x7f, 0x1f, 0x60, 0b101, 0b10].map(daysOf), ['all', 'weekdays', 'weekend', ['mon', 'wed'], 'tue']);
eq('time', ['08:00', '24:00', '7:5', '25:00', '08:60', 'x'].map(parseTime), [480, 1440, 425, null, null, null]);

// ---------------------------------------------------------------- routines as the game resolves them
const elena = {
  places: { home: 'casa', work: 'aula' },
  routine: [
    { days: 'all', from: '21:00', to: '07:00', at: '$home', activity: 'sleep', priority: 15, id: 'dorme' },
    { days: 'weekdays', from: '08:00', to: '16:00', at: 'aula', activity: 'work', priority: 20 },
    { days: 'weekdays', from: '12:00', to: '13:00', at: 'mensa', activity: 'eat', priority: 20 },   // same priority, later: loses
    { days: 'sun', from: '10:00', to: '12:00', at: '$church', priority: 50 },                        // no such place: skipped
    'messa',
  ],
};
const schedules = { messa: { id: 'messa', blocks: [{ days: 'sun', from: '09:00', to: '11:00', at: 'chiesa', activity: 'pray', priority: 30 }] } };
const at = (dow, t) => { const w = whereAt(elena, schedules, dow, parseTime(t)); return [w.node, w.activity]; };
eq('Monday 10:00 at work', at(0, '10:00'), ['aula', 'work']);
eq('on a tie the first one wins', at(0, '12:30'), ['aula', 'work']);
eq('Monday 22:00 sleeping', at(0, '22:00'), ['casa', 'sleep']);
eq('Tuesday 06:00 still sleeping (block past midnight)', at(1, '06:00'), ['casa', 'sleep']);
eq('Monday 07:30 no block: at home', at(0, '07:30'), ['casa', '']);
eq('Saturday 10:00 no work', at(5, '10:00'), ['casa', '']);
eq('Sunday 10:30: $church is missing, the shared routine applies', at(6, '10:30'), ['chiesa', 'pray']);
eq('Monday 00:30 comes from Sunday evening', at(0, '00:30'), ['casa', 'sleep']);
eq('blocks split at midnight', routineBlocks([{ days: 'sun', from: '21:00', to: '07:00', at: 'x' }]).map((b) => [b.mask, b.start, b.end]), [[0x40, 1260, 1440], [0x01, 0, 420]]);

// ---------------------------------------------------------------- checks
const refs = { node: ['casa', 'aula', 'mensa', 'chiesa'], item: ['pane'], stat: ['energy'], activity: ['sleep', 'work'], schedule: ['messa'], images: [] };
const c = checkRecord('npc', { id: 'x', places: { home: 'casa' }, routine: [{ from: '8:00', to: '99:00', at: 'luna' }, 'boh'], inventory: { sasso: 1, pane: 0 } }, refs);
eq('character errors', c.errors.length, 4);       // time, place, shared routine, item
eq('character warnings', c.warnings.length, 1);     // amount 0
eq('id already taken', checkRecord('item', { id: 'pane' }, refs, ['pane']).errors.length, 1);
eq('rules: baseline out of range', checkRecord('rules', { stats: [{ id: 'a', min: 0, max: 10, baseline: 20 }] }, refs).warnings.length, 1);

// ---------------------------------------------------------------- effects as lines
const fx = [{ do: 'setStat', stat: 'energy', value: 100 }, { do: 'advanceTimeTo', time: '07:00' }];
eq('effects to lines', effectsToText(fx), 'setStat(energy, 100)\nadvanceTimeTo("07:00")');
eq('lines to effects', effectsFromText(effectsToText(fx), null).effects, fx);
eq('correct lines: no errors or warnings', [effectsFromText('modStat(happiness, 5)', null).errors, effectsFromText('modStat(happiness, 5)', null).warnings], [[], []]);
eq('line number of the error', effectsFromText('setStat(energy, 1)\nsetStat(energy 5)', null).errors[0]?.startsWith('line 2:'), true);
eq('lines with an error', effectsFromText('setStat(energy 100)', null).errors.length > 0, true);

// ---------------------------------------------------------------- title.json (System tab)
const tRefs = { images: ['bg.png', 'Buttons/a.png', 'Buttons/b.png'], music: ['tema.mp3'] };
eq('title.json correct', checkRecord('title', { image: 'bg.png', music: 'tema.mp3', musicOn: true, drawTitle: false, volume: 0.6,
  buttons: { normal: 'Buttons/a.png', hover: 'Buttons/b.png', border: 28, textColor: '#4a2516' } }, tRefs), { errors: [], warnings: [] });
const tBad = checkRecord('title', { image: '../x.png', music: 'tema.txt', drawTitle: 'si', volume: 3,
  buttons: { hover: 'Buttons/manca.png', border: -1, textColor: 'marroncino' }, menuButtons: 'no' }, tRefs);
eq('title.json errors', tBad.errors.length, 5);       // ../, extension, drawTitle, border, menuButtons
eq('title.json warnings', tBad.warnings.length, 4);     // volume, normal missing, hover not found, colour
eq('music off: the file is not checked', checkRecord('title', { musicOn: false, music: 'manca.mp3' }, tRefs), { errors: [], warnings: [] });

// ---------------------------------------------------------------- hud.json (System tab) and the character profile
const hRefs = { images: ['hud/diary.png'], stat: ['energy', 'happiness'], quest: ['crypt'], item: ['map'] };
eq('hud.json correct', checkRecord('hud', { align: 'left', size: 72,
  buttons: [{ id: 'status', icon: 'hud/diary.png', label: { en: 'Me' }, open: 'status', key: 'c' },
    { id: 'map', icon: 'hud/diary.png', when: { type: 'hasItem', item: 'map' } }],
  status: { stats: ['energy'] }, npcs: { stats: ['happiness'], show: ['age', 'where'] } }, hRefs), { errors: [], warnings: [] });
const hBad = checkRecord('hud', { align: 'top', size: 500,
  buttons: [{ id: 'a', icon: 'hud/none.png', open: 'inventory', key: 'F5' }, { id: 'a', icon: 'hud/diary.png', open: 'map', key: 'x' },
    { label: 'no id' }, { id: 'b', icon: 'hud/diary.png', key: 'X', when: { type: 'quest', quest: 'nope' } }],
  status: { stats: ['mana'] }, npcs: { show: ['shoe'] } }, hRefs);
eq('hud.json errors', hBad.errors.length, 4);          // open, key F5, same id, no id
eq('hud.json warnings', hBad.warnings.length, 8);      // align, size, picture missing, map, key twice, unknown quest, stat, show
eq('profile correct', checkRecord('npc', { id: 'e', places: { home: 'h' }, profile: { image: 'hud/diary.png', when: { type: 'quest', quest: 'crypt' },
  hints: ['Plain', { en: 'Two', it: 'Due' }, { text: 'Later', when: { type: 'quest', quest: 'crypt', status: 'active' } }] } }, { ...hRefs, node: ['h'] }), { errors: [], warnings: [] });
const pBad = checkRecord('npc', { id: 'e', places: { home: 'h' }, profile: { image: 'x.png', hints: [3, '', { text: 'x', when: { type: 'quest', quest: 'nope' } }] } }, { ...hRefs, node: ['h'] });
eq('profile problems', [pBad.errors.length, pBad.warnings.length], [1, 3]);     // 3 is no text; picture, empty, unknown quest
const cond = { all: [{ type: 'quest', quest: 'crypt', status: 'active' }, { any: [{ type: 'flag', key: 'met' }, { not: { type: 'hasItem', item: 'map' } }] }] };
eq('condition as lines and back', condFromText(condToText(cond), hRefs).cond, cond);
eq('one condition stays one', condFromText('quest(crypt, completed)', hRefs).cond, { type: 'quest', quest: 'crypt', status: 'completed' });
eq('empty condition = always', condFromText(['  ', ' '].join('\n'), hRefs).cond, undefined);
eq('condition with a mistake', condFromText(['quest(crypt', 'flag(a)'].join('\n'), hRefs).errors[0]?.startsWith('line 1:'), true);

// ---------------------------------------------------------------- actions.json (System tab) and action messages
const aRefs = { npc: ['lia', 'elena'], images: ['a/talk.png'], quest: ['crypt'] };
eq('actions.json correct', checkRecord('actions', { actions: [{ id: 'talk', label: 'Talk to {npc}', icon: 'a/talk.png', do: 'talk' },
  { id: 'rob', npcs: ['lia'], tags: ['guard'], when: { type: 'relation', from: 'self', to: 'player', op: '<', value: 0 } }] }, aRefs), { errors: [], warnings: [] });
const aBad = checkRecord('actions', { actions: [{ id: 'x', do: 'fly', npcs: ['nobody'], icon: 'none.png' }, { id: 'x', do: 'trade' }, { label: 'no id' },
  { id: 'y', tags: 'guard', when: { type: 'quest', quest: 'nope' } }] }, aRefs);
eq('actions.json errors', aBad.errors.length, 4);       // do, same id, no id, tags not a list
eq('actions.json warnings', aBad.warnings.length, 4);   // nobody, picture, trade, unknown quest
const nested = checkRecord('actions', { pageSize: 4, labels: { next: 'Avanti', back: 'Indietro', close: 'Chiudi' }, actions: [
  { id: 'scenes', label: 'Review scenes', pageSize: 5, actions: [{ id: 'scene_1' }, { id: 'scene_2', when: { type: 'quest', quest: 'crypt' } }] }] }, aRefs);
eq('sub-actions correct', nested, { errors: [], warnings: [] });
const nBad = checkRecord('actions', { labels: { forward: 'x' }, actions: [{ id: 'a', do: 'talk', pageSize: -1, actions: [{ id: 'a' }, { id: 'b', pageSize: 3 }] }, { id: 'c', pageSize: 2 }] }, aRefs);
eq('sub-actions problems', [nBad.errors.length, nBad.warnings.length], [1, 5]);      // same id in a sub-action; labels, do ignored, pageSize -1, pageSize without sub-actions ×2
eq('self in an action condition', condFromText('relation(self, player, "<", 0)', aRefs, { self: true }).warnings, []);
eq('self elsewhere is an unknown character', condFromText('relation(self, player, "<", 0)', aRefs).warnings.length, 1);

// ---------------------------------------------------------------- where a quest is used (Delete in Quests & messages)
const qFiles = [
  { rel: 'quests/q_a.json', json: { id: 'q_a', stages: [{ id: 's' }] } },
  { rel: 'events/q_b/ev.json', json: { id: 'ev', when: { type: 'quest', quest: 'q_a', status: 'completed' }, script: [{ do: 'startQuest', quest: 'q_b' }] } },
  { rel: 'events/x.json', json: { id: 'x', script: [{ if: { type: 'quest', quest: 'q_a' }, then: [{ do: 'completeQuest', quest: 'q_a' }] }] } },
];
eq('uses of a quest', usages('quest', 'q_a', qFiles), [
  { rel: 'events/q_b/ev.json', where: 'condition quest' },
  { rel: 'events/x.json', where: 'condition quest' },
  { rel: 'events/x.json', where: 'command completeQuest' },
]);

// ---------------------------------------------------------------- every world and test pack
const packs = [];
const worlds = join(here, '..', '..', 'worlds');
if (existsSync(worlds)) for (const w of readdirSync(worlds)) if (existsSync(join(worlds, w, 'base'))) packs.push(join(worlds, w, 'base'));
const fixtures = join(here, '..', '..', 'unity', 'LivingNPC', 'SmokeFixtures');
for (const sub of ['worlds', 'smoke_mods']) {
  const dir = join(fixtures, sub);
  if (!existsSync(dir)) continue;
  for (const p of readdirSync(dir)) {
    if (existsSync(join(dir, p, 'base'))) packs.push(join(dir, p, 'base'));
    else if (existsSync(join(dir, p, 'pack.json'))) packs.push(join(dir, p));
  }
}

// a mod pack uses the ids of the world it is loaded on (the smoke world)
const smokeWorld = join(fixtures, 'worlds', 'smoke_world', 'base');
const baseRefs = existsSync(smokeWorld) ? await packRefs(smokeWorld) : {};
const merge = (a, b) => Object.fromEntries([...new Set([...Object.keys(a), ...Object.keys(b)])].map((k) => [k, Array.isArray(a[k]) || Array.isArray(b[k]) ? [...(a[k] || []), ...(b[k] || [])] : { ...(a[k] || {}), ...(b[k] || {}) }]));

for (const pack of packs) {
  const data = await listRecords(pack);
  const own = await packRefs(pack);
  const r = pack.replace(/\\/g, '/').includes('/smoke_mods/') ? merge(baseRefs, own) : own;
  let errors = 0, warnings = 0, files = 0;
  for (const kind of ['npc', 'schedule', 'item']) for (const rec of data[kind]) {
    files++;
    const res = checkRecord(kind, rec.json, r);
    errors += res.errors.length; warnings += res.warnings.length;
    for (const e of res.errors) console.log(`  ${rec.rel}: ${e}`);
    // the effects of the sleep and every routine survive the trip through the form
  }
  for (const kind of ['title', 'hud', 'actions']) if (data[kind]?.json) {
    files++;
    const res = checkRecord(kind, data[kind].json, r);
    errors += res.errors.length; warnings += res.warnings.length;
    for (const e of [...res.errors, ...res.warnings]) console.log(`  ${kind}.json: ${e}`);
  }
  // every condition of the profiles and of the bar survives the trip through the form
  for (const c of [...data.npc.flatMap((x) => [x.json?.profile?.when, ...(x.json?.profile?.hints || []).map((hh) => hh?.when)]), ...(data.hud?.json?.buttons || []).map((b) => b?.when)].filter(Boolean))
    eq(`${pack}: condition ${JSON.stringify(c)} as lines and back`, condFromText(condToText(c), r).cond, c);
  for (const c of (data.actions?.json?.actions || []).map((x) => x?.when).filter(Boolean))
    eq(`${pack}: action condition ${JSON.stringify(c)} as lines and back`, condFromText(condToText(c), r, { self: true }).cond, c);
  if (data.rules?.json) {
    files++;
    const res = checkRecord('rules', data.rules.json, r);
    errors += res.errors.length; warnings += res.warnings.length;
    for (const e of res.errors) console.log(`  rules.json: ${e}`);
    const sleepFx = data.rules.json.sleep?.effects;
    if (sleepFx) eq(`${pack}: effetti del sonno andata e ritorno`, effectsFromText(effectsToText(sleepFx), r).effects, sleepFx);
  }
  // every npc used as a speaker or giver is found by "where is it used"
  const all = await allJson(pack);
  for (const npc of data.npc.filter((x) => x.json?.id)) {
    const used = all.some(({ json }) => JSON.stringify(json).includes(`"${npc.json.id}"`) && json !== npc.json);
    const found = usages('npc', npc.json.id, all).length > 0;
    if (found && !used) { failed++; console.log(`FAILED uses of ${npc.json.id}: found but the id does not appear`); }
  }
  console.log(`${pack.replace(/\\/g, '/').split('/').slice(-3).join('/')}: ${files} files, ${errors} errors, ${warnings} warnings`);
}

console.log(`\n${passed} checks passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
