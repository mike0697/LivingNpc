// node tools/convert-pack.mjs <pack folder> [--write]
// Reads every document of the pack, turns it into a script and back into JSON. Without --write it only reports what
// would change; with --write it rewrites the files in the format the program writes (old event forms moved into "script").

import { resolve } from 'node:path';
import { listDocs, readDoc, saveFiles } from '../lib/store.js';
import { docScript, docFiles } from '../lib/docs.js';

const pack = process.argv[2] && resolve(process.argv[2]);
const write = process.argv.includes('--write');
if (!pack) { console.error('usage: node tools/convert-pack.mjs <pack folder> [--write]'); process.exit(2); }

let problems = 0;
for (const doc of await listDocs(pack)) {
  const out = docFiles(docScript(await readDoc(pack, doc)), doc);
  if (out.errors.length) {
    problems++;
    console.log(`${doc.id}: ${out.errors.length} errori`);
    for (const e of out.errors) console.log(`  line ${e.line}: ${e.message}`);
    continue;
  }
  const changed = write ? await saveFiles(pack, out.write, out.remove) : Object.keys(out.write);
  console.log(`${doc.id}: ${write ? `${changed.length} files changed` : `${changed.length} files`}`);
}
process.exit(problems ? 1 : 0);
