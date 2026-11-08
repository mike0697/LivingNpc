// node tests/convert.mjs: the converter on every real document and on hand-written examples.
// 1. every document of every pack: JSON -> script -> JSON -> script gives the same script, with no errors;
// 2. a script in the new format gives back exactly its JSON;
// 3. errors and warnings point at the right line.

import assert from 'node:assert/strict';
import { existsSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { listDocs, readDoc, refs } from '../lib/store.js';
import { docScript, docFiles } from '../lib/docs.js';
import { toScript, fromScript } from '../lib/script.js';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
let failed = 0, passed = 0;
function test(name, fn) {
  return Promise.resolve().then(fn).then(() => { passed++; }, (e) => { failed++; console.error(`FAIL ${name}\n  ${e.message.split('\n').join('\n  ')}`); });
}

const packs = [];
const worlds = join(root, 'worlds');
if (existsSync(worlds)) for (const w of readdirSync(worlds)) if (existsSync(join(worlds, w, 'base'))) packs.push(join(worlds, w, 'base'));
const smoke = join(root, 'unity', 'LivingNPC', 'SmokeFixtures', 'smoke_mods');
if (existsSync(smoke)) for (const p of readdirSync(smoke)) packs.push(join(smoke, p));
const fixture = join(root, 'unity', 'LivingNPC', 'SmokeFixtures', 'worlds', 'smoke_world', 'base');
if (existsSync(fixture)) packs.push(fixture);

let docsSeen = 0;
for (const pack of packs) {
  for (const doc of await listDocs(pack)) {
    docsSeen++;
    await test(`${pack} ${doc.id}`, async () => {
      const files = await readDoc(pack, doc);
      const s1 = docScript(files);
      const out = docFiles(s1, doc);
      assert.deepEqual(out.errors, [], `errors:\n${out.errors.map((e) => `${e.line}: ${e.message}`).join('\n')}\n--- script ---\n${s1}`);
      const again = Object.entries(out.write).map(([rel, text]) => ({ rel, json: JSON.parse(text) }));
      assert.equal(docScript(again), s1);
    });
  }
}

// the new format, written by hand: back to the very same JSON
const sequence = {
  id: 'ev_seq',
  trigger: 'talk',
  npc: 'elena',
  priority: 30,
  when: { all: [{ type: 'quest', quest: 'q', status: 'active', stage: 'two' }, { any: [{ type: 'flag', key: 'a', op: '>=', value: 1 }, { not: { type: 'playerAt', node: 'casa' } }] }] },
  script: [
    { say: 'uno', who: 'elena' },
    { say: 'due', who: 'elena', pose: 'happy' },
    { say: 'tre', who: 'Guardia del ponte' },
    { say: 'quattro' },
    { show: 'lab', file: 'lab.png', x: 10, y: 20, width: 30 },
    { do: 'giveItem', item: 'bread', count: 2, target: 'elena' },
    { if: { type: 'flag', key: 'x', op: '>=', value: 1 }, then: [{ say: 'si' }], else: [{ if: { type: 'always' }, then: [{ say: 'elif' }], else: [{ say: 'no' }] }] },
    { choices: [
      { text: 'A', then: [{ do: 'setFlag', key: 'pick', value: 1 }, { hide: 'lab' }] },
      { text: { it: 'B', en: 'B!' }, when: { type: 'hasItem', item: 'bread', op: '>=', count: 1 }, then: [] },
    ] },
    { sound: 'click.wav', volume: 0.5 },
    { do: 'message', text: 'fine' },
  ],
};
const quest = {
  id: 'q',
  title: 'Prova',
  giver: 'elena',
  autoStart: { type: 'playerAt', node: 'casa' },
  onStart: [{ do: 'message', text: 'via' }],
  stages: [
    { id: 'one', text: 'Primo', complete: { type: 'always' }, onComplete: [{ do: 'giveItem', item: 'bread' }] },
    { id: 'two', text: 'Secondo', fail: { type: 'elapsed', since: 'stage', days: 1 }, next: 'end' },
  ],
};

await test('new format round trip', () => {
  const text = toScript([{ kind: 'quest', json: quest }, { kind: 'event', json: sequence }]);
  const back = fromScript(text);
  assert.deepEqual(back.errors, []);
  assert.deepEqual(back.units.map((u) => u.json), [quest, sequence], text);
});

const template = {
  id: 't_bread',
  title: '{self} brings bread to {friend}',
  pauseDays: 2,
  when: { type: 'hasItem', item: 'bread', target: 'self' },
  weight: { base: 5, stats: { happiness: 0.2 }, goals: { help: 30 }, relations: { friend: 0.5 } },
  slots: [
    { id: 'friend', type: 'npc', free: true, when: { type: 'relation', from: 'self', to: 'it', op: '>=', value: 10 } },
    { id: 'home', type: 'place', of: 'friend', place: 'home' },
    { id: 'gift', type: 'item', items: ['bread', 'gold_coin'] },
  ],
  texts: {
    intro: [
      { when: { type: 'stat', stat: 'happiness', op: '>=', value: 60, target: 'self' }, lines: ['On my way to {friend}!', { it: 'Vado da {friend}', en: 'Going to {friend}' }] },
      { lines: ['I should see {friend:him|her}.'] },
    ],
    talk: ['Not now, I am busy.'],
  },
  onStart: [{ do: 'tell', lines: 'intro' }],
  onComplete: [{ do: 'modRelation', from: 'friend', to: 'self', delta: 5 }],
  stages: [
    { id: 'go', text: 'Going to {home}', onStart: [{ do: 'moveNpc', npc: 'self', node: 'home', forMinutes: 120 }], complete: { type: 'npcAt', npc: 'self', node: 'home' }, fail: { type: 'elapsed', hours: 6 } },
    { id: 'give', complete: { all: [{ type: 'elapsed', hours: 1 }, { type: 'chance', percent: 70 }] }, onComplete: [{ do: 'giveItem', item: 'gift', target: 'friend' }], next: 'end' },
  ],
};
const lines = { id: 'greet', variants: [{ when: { type: 'relation', from: 'self', to: 'player', op: '>', value: 20 }, lines: ['Hello, {npc}!'] }, { lines: ['Hi.'] }] };
const plainLines = { id: 'busy', variants: ['Later.', 'Not now.'] };

await test('automatic quests and shared lines round trip', () => {
  const text = toScript([{ kind: 'template', json: template }, { kind: 'lines', json: lines }, { kind: 'lines', json: plainLines }]);
  const back = fromScript(text);
  assert.deepEqual(back.errors, [], text);
  assert.deepEqual(back.units.map((u) => u.json), [template, lines, plainLines], text);
  assert.equal(toScript(back.units), text);
});

await test('automatic quests: self and slots are known ids, typos are not', () => {
  const r = { npc: ['elena'], node: ['square'], item: ['bread'], stat: [], goal: [], template: [], lines: [], quest: [], stage: {} };
  const text = [
    'template t_x:',
    '    slot friend:',
    '        type = npc',
    '        when:',
    '            npcAt(it, square)',
    '    slot spot:',
    '        type = place',
    '        of = friend',
    '        place = home',
    '    texts:',
    '        intro:',
    '            "Hi {friend}"',
    '    stage go:',
    '        onStart:',
    '            moveNpc(self, spot)',
    '            tell(intro)',
    '            giveItem(bread, target=frend)',
    '            tell(nope)',
    '        complete:',
    '            npcAt(friend, spot)',
  ].join('\n');
  const out = fromScript(text, r);
  assert.deepEqual(out.errors, []);
  assert.deepEqual(out.warnings.map((w) => w.line), [17, 18], JSON.stringify(out.warnings));
});

await test('old event forms move into the script', () => {
  const old = { id: 'ev', trigger: 'talk', npc: 'elena', text: 'Ciao', popup: true, effects: [{ do: 'setFlag', key: 'k' }], choices: [{ text: 'Ok', effects: { do: 'advanceQuest', quest: 'q' } }] };
  const back = fromScript(toScript([{ kind: 'event', json: old }]));
  assert.deepEqual(back.errors, []);
  assert.deepEqual(back.units[0].json, {
    id: 'ev', trigger: 'talk', npc: 'elena', text: 'Ciao',
    script: [{ do: 'setFlag', key: 'k' }, { say: 'Ciao', who: 'elena' }, { choices: [{ text: 'Ok', then: [{ do: 'advanceQuest', quest: 'q' }] }] }],
  });
});

await test('hand-written script', () => {
  const text = [
    'event ev_a:   # commento',
    '    trigger = enterNode',
    '    node = casa',
    '    when:',
    '        playerAt(casa)',
    "    say('hello # not a comment')",
    '    if any(flag(a), flag(b, ">=", 2)):',
    '        giveItem(bread)',
    '    else:',
    '        pass',
  ].join('\n');
  const r = fromScript(text);
  assert.deepEqual(r.errors, []);
  assert.deepEqual(r.units[0].json, {
    id: 'ev_a', trigger: 'enterNode', node: 'casa', when: { type: 'playerAt', node: 'casa' },
    script: [
      { say: 'hello # not a comment' },
      { if: { any: [{ type: 'flag', key: 'a' }, { type: 'flag', key: 'b', op: '>=', value: 2 }] }, then: [{ do: 'giveItem', item: 'bread' }], else: [] },
    ],
  });
});

await test('errors point at their line', () => {
  const r = fromScript([
    'event ev_b:',
    '    trigger = talk',          // no npc: error on line 1
    '    elena: "ok"',
    '        giveItem(bread)',      // line 4: indented under a line that opens nothing
    '    flag(x)',                  // line 5: a condition used as a command
    '    fooBar(1)',                // line 6: unknown command (warning, positional value: error)
    '    "not closed',              // line 7
    'quest q_c:',
    '    stage s:',
    '        onComplete:',
    '            elena: "no"',      // line 11: no lines in quest effects
  ].join('\n'));
  const lines = r.errors.map((e) => e.line).sort((a, b) => a - b);
  assert.deepEqual(lines, [1, 4, 5, 6, 7, 11], JSON.stringify(r.errors, null, 1));
});

await test('effect blocks of a quest refuse dialogue', () => {
  const r = fromScript('quest q:\n    onStart:\n        choice:\n            option "a":\n                pass\n    stage s:\n        text = "x"\n');
  assert.equal(r.errors.length, 1);
  assert.equal(r.errors[0].line, 3);
});

await test('ids checked against the world', async () => {
  const pack = join(root, 'worlds', 'aetherhall', 'base');
  if (!existsSync(pack)) return;
  const r = await refs(pack);
  for (const doc of await listDocs(pack)) {
    const out = docFiles(docScript(await readDoc(pack, doc)), doc, r);
    assert.deepEqual(out.warnings, [], `${doc.id}: ${JSON.stringify(out.warnings)}`);
  }
  const typo = fromScript('event ev_t:\n    trigger = talk\n    npc = elen\n    giveItem(pane)\n    elena: "ok"\n    show(x, "nope.png")\n', r);
  assert.deepEqual(typo.warnings.map((w) => w.line), [3, 4, 6], JSON.stringify(typo.warnings));
});

console.log(`${passed} ok, ${failed} failed (${docsSeen} documents from ${packs.length} packs)`);
process.exit(failed ? 1 : 0);
