// Tests the built editor on temporary worlds: the real worlds are never touched. Run "npm run build" first.
import { chromium } from '@playwright/test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';

const here = dirname(fileURLToPath(import.meta.url));
const PORT = 7110;
const URL_BASE = `http://127.0.0.1:${PORT}`;
const root = mkdtempSync(join(tmpdir(), 'livingnpc-map-editor-'));
const worlds = join(root, 'worlds');
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const json = (file) => JSON.parse(readFileSync(file, 'utf8'));
const write = (file, data) => { mkdirSync(dirname(file), { recursive: true }); writeFileSync(file, typeof data === 'string' || Buffer.isBuffer(data) ? data : JSON.stringify(data, null, 2)); };

const png = await sharp({ create: { width: 640, height: 360, channels: 3, background: '#335577' } }).png().toBuffer();
const pack = join(worlds, 'prova', 'base');
function node(id, links = []) {
  write(join(pack, 'nodes', id, 'bg.png'), png);
  write(join(pack, 'nodes', id, 'node.json'), { id, title: id, text: '', backgrounds: { day: 'bg.png' }, links, tags: ['tenuto'] });
}
// Like a world edited by hand: project.json lists a map renamed by hand and misses two others.
write(join(pack, 'pack.json'), { id: 'base', name: 'Prova' });
write(join(pack, 'rules.json'), { tocca: 'mai' });
write(join(pack, 'project.json'), { format: 'livingnpc-map', version: 1, name: 'Prova', startNode: 'letto', nodes: [{ id: 'letto', graph: { x: 400, y: 300 } }, { id: 'vecchio_id', graph: { x: 500, y: 400 } }] });
node('letto'); node('biblioteca'); node('orfana');
write(join(worlds, 'prova', 'project.json'), { residuo: 'cartella padre' });       // must never be read or written
write(join(pack, 'sotto', 'pack.json'), { residuo: 'cartella figlia' });
write(join(worlds, 'padre', 'pack.json'), {});                                     // pack in the world folder
write(join(worlds, 'figlio', 'base', 'base', 'pack.json'), {});                    // pack one folder too deep
write(join(root, 'altrove', 'pack.json'), {});
mkdirSync(join(worlds, 'collegato'));
symlinkSync(join(root, 'altrove'), join(worlds, 'collegato', 'base'), 'junction');

const server = spawn(process.execPath, ['server.mjs'], { cwd: join(here, '..'), stdio: 'pipe', env: { ...process.env, PORT: String(PORT), LIVINGNPC_WORLDS: worlds } });
let browser;
try {
  for (let i = 0; i < 50; i++) { try { if ((await fetch(URL_BASE)).ok) break; } catch {} await sleep(100); }
  const post = (world, body, headers = { 'x-livingnpc-map-editor': '1' }) => fetch(`${URL_BASE}/api/save?world=${encodeURIComponent(world)}`, { method: 'POST', headers, body: JSON.stringify(body) });

  // Hierarchy: a wrong folder is neither opened nor saved
  const listed = (await (await fetch(`${URL_BASE}/api/worlds`)).json()).worlds;
  assert.match(listed.find((w) => w.name === 'padre').error, /world folder/);
  assert.match(listed.find((w) => w.name === 'figlio').error, /too deep/);
  assert.match(listed.find((w) => w.name === 'collegato').error, /link to another folder/);
  const loaded = await (await fetch(`${URL_BASE}/api/world?world=prova`)).json();
  const base = { project: loaded.project, revision: loaded.revision };
  for (const world of ['padre', 'figlio', 'collegato']) assert.equal((await post(world, base)).status, 422, world);
  for (const world of ['prova/base', 'prova\\base', '..', '../..', 'inesistente', '']) assert.equal((await post(world, base)).status, 404, `world “${world}”`);
  const evil = (id) => ({ ...base, project: { ...loaded.project, startNode: id, nodes: [{ ...loaded.project.nodes[0], id }] } });
  for (const id of ['..', '../..', '../../padre', 'a/b', 'a\\b', 'x/../..', '']) assert.equal((await post('prova', evil(id))).status, 422, `id «${id}»`);
  const badImage = structuredClone(loaded.project);
  badImage.nodes[0].backgrounds.day = '/maps/altro/nodes/letto/bg.png';
  assert.equal((await post('prova', { ...base, project: badImage })).status, 422, 'image from another world');
  const badFolder = await post('prova', { ...base, remove: ['..'] });
  assert.equal(badFolder.status, 422, 'folder to delete outside nodes');
  assert.equal((await post('prova', base, {})).status, 403, 'write without header');
  assert.deepEqual(json(join(worlds, 'prova', 'project.json')), { residuo: 'cartella padre' });
  assert.ok(!existsSync(join(worlds, 'project.json')));
  assert.ok(!existsSync(join(pack, 'nodes', 'nodes')));

  browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', (error) => errors.push(error.message));
  const result = page.locator('.result-bar');
  const button = (name) => page.getByRole('button', { name, exact: true });
  const pick = (name) => page.locator('.world-row').filter({ hasText: name });

  // Start: asks for the world, the button says what it does, broken worlds cannot be clicked
  await page.goto(URL_BASE);
  await page.getByRole('dialog', { name: 'Load World' }).waitFor();
  assert.equal(await page.locator('input[type=file][webkitdirectory]').count(), 0);
  assert.ok(await pick('padre').isDisabled());
  await pick('prova').click();
  await result.waitFor();
  assert.match(await result.innerText(), /Loaded “prova”: 3 maps from worlds\/prova\/base/);
  assert.equal(await page.locator('.node-list-item').count(), 3, 'like the game, counts the folders in nodes/');
  await result.getByRole('button', { name: /Show/ }).click();
  assert.match(await result.innerText(), /vecchio_id/);
  assert.match(await result.innerText(), /worlds\/prova\/project\.json/);
  assert.match(await result.innerText(), /worlds\/prova\/base\/sotto/);
  assert.match(await page.locator('.node-list-item img').first().getAttribute('src'), /^\/thumb/);
  assert.ok(await page.locator('.node-list-item img').first().evaluate((img) => img.complete || new Promise((r) => { img.onload = r; })) !== false);
  assert.ok(await page.locator('.node-list-item img').first().evaluate((img) => img.naturalWidth === 320), 'thumbnail scaled down');

  // The disk wins: a change made outside shows after reloading the page
  const edited = json(join(pack, 'nodes', 'letto', 'node.json'));
  write(join(pack, 'nodes', 'letto', 'node.json'), { ...edited, title: 'Modificato a mano' });
  await page.reload();
  await page.locator('.node-list-item').filter({ hasText: 'Modificato a mano' }).waitFor();

  // The draft is offered, never applied on its own
  await page.locator('.sidebar-status').getByText('Saved to disk').waitFor();
  await page.locator('.inspector textarea').fill('testo in bozza');
  await page.locator('.sidebar-status').getByText('Unsaved changes').waitFor();
  await sleep(900);
  page.once('dialog', (dialog) => dialog.accept());
  await page.reload();
  await page.getByRole('alertdialog', { name: 'Unsaved work found' }).waitFor();
  await button('Restore the draft').click();
  await page.locator('.sidebar-status').getByText('Unsaved changes').waitFor();
  assert.equal(await page.locator('.inspector textarea').inputValue(), 'testo in bozza');

  // Save World: writes only what is needed, in the right place, and says so
  const before = readFileSync(join(pack, 'nodes', 'orfana', 'node.json'), 'utf8');
  await button('Save World').click();
  await result.getByText(/Saved to worlds\/prova\/base/).waitFor();
  assert.match(await result.innerText(), /unchanged/);
  await page.locator('.sidebar-status').getByText('Saved to disk').waitFor();
  assert.equal(json(join(pack, 'nodes', 'letto', 'node.json')).text, 'testo in bozza');
  assert.deepEqual(json(join(pack, 'nodes', 'letto', 'node.json')).tags, ['tenuto'], 'extra fields are kept');
  assert.deepEqual(json(join(pack, 'project.json')).nodes.map((n) => n.id).sort(), ['biblioteca', 'letto', 'orfana']);
  assert.equal(readFileSync(join(pack, 'nodes', 'orfana', 'node.json'), 'utf8').length > 0, true);
  assert.ok(before);
  assert.deepEqual(json(join(pack, 'rules.json')), { tocca: 'mai' });
  assert.deepEqual(json(join(worlds, 'prova', 'project.json')), { residuo: 'cartella padre' });
  assert.deepEqual(json(join(pack, 'sotto', 'pack.json')), { residuo: 'cartella figlia' });
  assert.deepEqual(readdirSync(join(worlds, 'prova')).sort(), ['base', 'project.json']);

  // Disk changed after loading: never overwrites without asking
  await page.locator('.inspector textarea').fill('mia modifica');
  write(join(pack, 'nodes', 'biblioteca', 'node.json'), { ...json(join(pack, 'nodes', 'biblioteca', 'node.json')), text: 'scritto fuori' });
  await page.keyboard.press('Control+s');
  await page.getByRole('alertdialog', { name: 'The files on disk have changed' }).waitFor();
  await button('Reload from disk').click();
  await result.getByText(/Loaded “prova”/).waitFor();
  assert.equal(await page.locator('.inspector textarea').inputValue(), 'testo in bozza');

  // New image and temporary id: no base64, the temporary folder does not stay
  await page.locator('.add-node').click();
  await page.locator('.inspector > input[type=file]').setInputFiles({ name: 'nuova.png', mimeType: 'image/png', buffer: png });
  await result.getByText(/nuova\.png/).waitFor();
  assert.match(await page.locator('.scene-stage > img').getAttribute('src'), /^\/uploads\//);
  await page.keyboard.press('Control+s');
  await result.getByText(/Saved to/).waitFor();
  assert.ok(existsSync(join(pack, 'nodes', 'new_place_4', 'bg.png')));
  await page.locator('.node-properties input').first().fill('Stanza');
  await page.locator('.node-properties input').first().press('Enter');
  await page.locator('.node-id code').getByText('stanza_1').waitFor();
  await page.keyboard.press('Control+s');
  await result.getByText(/1 folder deleted/).waitFor();
  assert.ok(existsSync(join(pack, 'nodes', 'stanza_1', 'bg.png')));
  assert.ok(!existsSync(join(pack, 'nodes', 'new_place_4')), 'for the game it would be one more map');

  // A WebP picture stays WebP (the game reads it): bg.webp, not a PNG copy
  const webp = await sharp(png).webp({ quality: 80 }).toBuffer();
  await page.locator('#bg-day').setInputFiles({ name: 'nuova.webp', mimeType: 'image/webp', buffer: webp });
  await page.locator('.background-row span').getByText('bg.webp').waitFor();
  await page.keyboard.press('Control+s');
  await result.getByText(/Saved to/).waitFor();
  assert.equal(readFileSync(join(pack, 'nodes', 'stanza_1', 'bg.webp')).subarray(8, 12).toString('latin1'), 'WEBP');
  assert.equal(json(join(pack, 'nodes', 'stanza_1', 'node.json')).backgrounds.day, 'bg.webp');

  // Deleting a map removes its folder only after confirming
  await page.locator('.node-list-item').filter({ hasText: 'orfana' }).click();
  page.once('dialog', (dialog) => dialog.accept());
  await page.getByRole('button', { name: 'Delete node' }).click();
  await button('Save World').click();
  await page.getByRole('alertdialog', { name: 'Delete these maps from disk?' }).waitFor();
  await button('Cancel').click();
  assert.ok(existsSync(join(pack, 'nodes', 'orfana')));
  await result.locator('.result-close').click();
  await button('Save World').click();
  await button('Delete and save').click();
  await result.getByText(/1 folder deleted/).waitFor();
  assert.ok(!existsSync(join(pack, 'nodes', 'orfana')));

  // Switching world with pending changes asks first
  await page.locator('.inspector textarea').fill('ancora da salvare');
  await button('Load World').click();
  await pick('prova').getByText('Reload from disk').click();
  await page.getByRole('alertdialog', { name: 'Unsaved changes' }).waitFor();
  await button('Cancel').click();
  assert.equal(await page.locator('.inspector textarea').inputValue(), 'ancora da salvare');

  // The game title is changed in LivingNPC Studio: saving the maps keeps the one on disk
  const now = await (await fetch(`${URL_BASE}/api/world?world=prova`)).json();
  write(join(pack, 'project.json'), { ...json(join(pack, 'project.json')), name: 'Title from LivingNPC Studio' });
  const renamed = await (await fetch(`${URL_BASE}/api/world?world=prova`)).json();
  assert.equal(renamed.project.name, 'Title from LivingNPC Studio');
  assert.equal((await post('prova', { project: { ...now.project, name: 'Vecchio titolo' }, revision: renamed.revision })).status, 200);
  assert.equal(json(join(pack, 'project.json')).name, 'Title from LivingNPC Studio');

  assert.deepEqual(errors, [], 'the console must have no errors');
  console.log('OK: folder hierarchy, loading, draft, saving, conflicts, images and deletions checked.');
} finally {
  if (browser) await browser.close();
  server.kill();
  await sleep(300);
  rmSync(root, { recursive: true, force: true });
}
