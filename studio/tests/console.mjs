// node tests/console.mjs: the debug console of the game reads commands as the Studio writes them.
// Its C# table of arguments (DebugConsole.cs) must match the "pos" parameters of lib/specs.js, and it must cover
// exactly the effects the compiler lets the console run (ContentCompiler.ConsoleEffects).

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { EFFECTS } from '../lib/specs.js';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const runtime = join(root, 'unity', 'LivingNPC', 'Assets', 'Game', 'Runtime');
const consoleCs = readFileSync(join(runtime, 'Presentation', 'DebugConsole.cs'), 'utf8');
const compilerCs = readFileSync(join(runtime, 'Content', 'ContentCompiler.cs'), 'utf8');

const names = (list) => [...list.matchAll(/"([^"]*)"/g)].map((m) => m[1]);

// ["modStat"] = (new[] { "stat", "delta" }, new[] { "target" }),
const table = {};
for (const m of consoleCs.matchAll(/\["(\w+)"\] = \(new(?:\[\]| string\[\]) \{([^}]*)\}, new(?:\[\]| string\[\]) \{([^}]*)\}\)/g))
  table[m[1]] = { positional: names(m[2]), named: names(m[3]) };

const block = compilerCs.match(/ConsoleEffects =\s*\{([^}]*)\}/);
assert.ok(block, 'ContentCompiler.ConsoleEffects not found');
const allowed = names(block[1]);

let failed = 0;
function check(name, fn) {
  try { fn(); } catch (e) { failed++; console.error(`FAIL ${name}\n  ${e.message.split('\n').join('\n  ')}`); }
}

check('the console table covers exactly ConsoleEffects', () => {
  assert.deepEqual(Object.keys(table).sort(), [...allowed].sort());
});

for (const [name, entry] of Object.entries(table)) {
  check(`${name} matches specs.js`, () => {
    const spec = EFFECTS[name];
    assert.ok(spec, `${name} is not an effect of specs.js`);
    assert.deepEqual(entry.positional, spec.params.filter((p) => p.pos).map((p) => p.name), 'arguments without a name');
    assert.deepEqual([...entry.named].sort(), spec.params.filter((p) => !p.pos).map((p) => p.name).sort(), 'arguments by name');
  });
}

if (failed) { console.error(`${failed} console check(s) failed`); process.exit(1); }
console.log(`console: ${Object.keys(table).length} commands match specs.js`);
