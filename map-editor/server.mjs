// Local helper: serves the built editor and reads / writes the worlds on disk, from any browser (no folder picker, no ZIP).
//
//   node server.mjs                      (start.bat does this)
//
// The disk is the only truth. The folder a world is saved to is derived here from the world's name and is always
// worlds/<world>/base: the browser never sends a path, and every write must match the shape checked by target().
// Worlds folder: env LIVINGNPC_WORLDS, else ../worlds. It only listens on 127.0.0.1.

import { createServer } from 'node:http';
import { createHash, randomUUID } from 'node:crypto';
import { createReadStream, existsSync, readdirSync, realpathSync } from 'node:fs';
import { copyFile, mkdir, readdir, readFile, rename, rm, stat, writeFile } from 'node:fs/promises';
import { basename, dirname, extname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import sharp from 'sharp';

const here = dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.env.PORT || 5173);
const WORLDS_DIR = resolve(here, process.env.LIVINGNPC_WORLDS || '../worlds');
const PACK = 'base';
const DIST = join(here, 'dist');
const UPLOADS = join(here, '.uploads');
const THUMBS = join(here, '.cache', 'thumbs');
const THUMB_WIDTH = 320;
const MAX_JSON = 16 * 1024 * 1024;
const MAX_IMAGE = 64 * 1024 * 1024;
const IMAGE_FILE = /\.(png|jpe?g|webp)$/i;
const NODE_ID = /^[A-Za-z0-9_-]+$/;
const VARIANT = /^[a-z0-9_]+$/;
const FILE_NAME = /^[^./\\:*?"<>|][^/\\:*?"<>|]*$/;
const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.webp': 'image/webp', '.ico': 'image/x-icon', '.woff2': 'font/woff2',
};

class HttpError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

const same = (a, b) => (process.platform === 'win32' ? a.toLowerCase() === b.toLowerCase() : a === b);
const packPath = (world) => `worlds/${world}/${PACK}`;

function listWorlds() {
  if (!existsSync(WORLDS_DIR)) return [];
  return readdirSync(WORLDS_DIR, { withFileTypes: true }).filter((e) => e.isDirectory() && !e.name.startsWith('.')).map((e) => e.name).sort();
}

/** Why a world cannot be opened or saved, or null. The contents must sit exactly in worlds/<world>/base: not one level up or down. */
function hierarchyError(world) {
  const dir = join(WORLDS_DIR, world);
  const pack = join(dir, PACK);
  if (!existsSync(join(pack, 'pack.json'))) {
    if (existsSync(join(dir, 'pack.json'))) return `pack.json is in the world folder: it must be in ${packPath(world)}`;
    if (existsSync(join(pack, PACK, 'pack.json'))) return `the pack is one folder too deep (${packPath(world)}/${PACK}): it must be in ${packPath(world)}`;
    return `${packPath(world)}/pack.json is missing`;
  }
  if (!same(realpathSync(pack), join(realpathSync(WORLDS_DIR), world, PACK))) return `${packPath(world)} is a link to another folder`;
  return null;
}

/** Files that look like a world's contents but sit one level above or below the pack: never read, never written, worth knowing about. */
function misplaced(world) {
  const dir = join(WORLDS_DIR, world);
  const pack = join(dir, PACK);
  const out = [];
  for (const name of ['project.json', 'nodes']) {
    if (existsSync(join(WORLDS_DIR, name))) out.push(`worlds/${name}`);
    if (existsSync(join(dir, name))) out.push(`worlds/${world}/${name}`);
  }
  for (const entry of readdirSync(pack, { withFileTypes: true })) {
    if (entry.isDirectory() && (existsSync(join(pack, entry.name, 'project.json')) || existsSync(join(pack, entry.name, 'pack.json')))) out.push(`${packPath(world)}/${entry.name}`);
  }
  return out;
}

/** The pack folder of a known world. This is the only place a save destination comes from. */
function packOf(world) {
  if (!listWorlds().includes(world)) throw new HttpError(404, `unknown world: ${world}`);
  const error = hierarchyError(world);
  if (error) throw new HttpError(422, error);
  return join(WORLDS_DIR, world, PACK);
}

/** Absolute path of a file Save may write: project.json or nodes/<id>/<file>, nothing above, beside or below them. */
function target(pack, rel) {
  const parts = rel.split('/');
  const shape = rel === 'project.json' || (parts.length === 3 && parts[0] === 'nodes' && NODE_ID.test(parts[1]) && (parts[2] === 'node.json' || (FILE_NAME.test(parts[2]) && IMAGE_FILE.test(parts[2]))));
  const abs = resolve(pack, rel);
  if (!shape || !abs.startsWith(pack + sep)) throw new HttpError(403, `path not allowed: ${rel}`);
  return abs;
}

/** Writes aside, then swaps: Unity never sees a half-written file. */
async function put(abs, write) {
  await mkdir(dirname(abs), { recursive: true });
  const tmp = join(dirname(abs), `.tmp-${randomUUID()}`);
  try { await write(tmp); await rename(tmp, abs); } catch (e) { await rm(tmp, { force: true }); throw e; }
}

async function readRaw(file) {
  try { return (await readFile(file, 'utf8')).replace(/^﻿/, ''); } catch { return null; }
}

function parse(text) {
  try { return JSON.parse(text); } catch { return undefined; }
}

/** What is on disk now, as the game sees it: every nodes/<folder>/node.json is a map, whatever project.json lists. */
async function snapshot(pack) {
  const nodesDir = join(pack, 'nodes');
  const folders = existsSync(nodesDir) ? (await readdir(nodesDir, { withFileTypes: true })).filter((e) => e.isDirectory() && !e.name.startsWith('.')).map((e) => e.name).sort() : [];
  const hash = createHash('sha1');
  const manifestText = await readRaw(join(pack, 'project.json'));
  hash.update(manifestText ?? '');
  const entries = [];
  for (const folder of folders) {
    const text = await readRaw(join(nodesDir, folder, 'node.json'));
    if (text === null) continue;
    hash.update(`\0${folder}\0${text}`);
    entries.push({ folder, text });
  }
  return { manifestText, entries, revision: hash.digest('hex') };
}

async function readWorld(world) {
  const pack = packOf(world);
  const disk = await snapshot(pack);
  const problems = misplaced(world).map((path) => `Misplaced file, ignored: ${path}`);
  const manifest = disk.manifestText === null ? {} : parse(disk.manifestText);
  if (manifest === undefined) throw new HttpError(422, `${packPath(world)}/project.json is not valid JSON`);

  const byId = new Map();
  for (const { folder, text } of disk.entries) {
    const data = parse(text);
    if (!data || typeof data !== 'object') { problems.push(`nodes/${folder}/node.json is not valid JSON: map ignored`); continue; }
    const id = data.id || folder;
    if (byId.has(id)) { problems.push(`Duplicate id “${id}” in nodes/${folder} and nodes/${byId.get(id).folder}: using the second one`); continue; }
    if (id !== folder) problems.push(`nodes/${folder} has the id “${id}”: the folder will take this name when saved`);
    const backgrounds = {};
    for (const [variant, file] of Object.entries(data.backgrounds || {})) {
      if (!existsSync(join(pack, 'nodes', folder, String(file)))) { problems.push(`nodes/${folder}: missing image ${file}`); continue; }
      backgrounds[variant] = id === folder ? file : `/maps/${encodeURIComponent(world)}/nodes/${folder}/${file}`;
    }
    byId.set(id, { folder, node: { ...data, id, title: data.title ?? id, text: data.text ?? '', links: data.links || [], backgrounds } });
  }

  const graphOf = new Map((manifest.nodes || []).map((entry) => [entry.id, entry.graph]));
  for (const id of graphOf.keys()) if (!byId.has(id)) problems.push(`project.json lists “${id}”, which no longer exists: removed from the list`);
  const ordered = [...graphOf.keys()].filter((id) => byId.has(id));
  for (const id of byId.keys()) {
    if (graphOf.has(id)) continue;
    problems.push(`“${id}” was not in project.json: added, as the game does`);
    ordered.push(id);
  }
  const nodes = ordered.map((id, index) => ({ ...byId.get(id).node, graph: graphOf.get(id) || { x: 200 + index * 40, y: 200 + index * 30 } }));
  for (const node of nodes) for (const link of node.links) if (!byId.has(link.target)) problems.push(`${node.id}: link to “${link.target}”, which does not exist`);

  let startNode = manifest.startNode;
  if (nodes.length && !byId.has(startNode)) {
    if (startNode) problems.push(`Start node “${startNode}” does not exist: using “${nodes[0].id}”`);
    startNode = nodes[0].id;
  }
  const blank = { id: 'new_place_1', title: 'New place', text: '', backgrounds: {}, links: [], graph: { x: 400, y: 300 } };
  return {
    world, pack: packPath(world), dir: pack, revision: disk.revision, folders: disk.entries.map((e) => e.folder), problems,
    project: { name: manifest.name || world, startNode: nodes.length ? startNode : blank.id, nodes: nodes.length ? nodes : [blank] },
  };
}

async function describeWorld(name) {
  const error = hierarchyError(name);
  if (error) return { name, pack: packPath(name), error };
  const disk = await snapshot(join(WORLDS_DIR, name, PACK));
  return { name, pack: packPath(name), maps: disk.entries.length, misplaced: misplaced(name).length };
}

/** Where the image a background refers to is now, and the file name it keeps in the node's folder. */
function imageSource(pack, world, node, variant, ref) {
  const upload = /^\/uploads\/([\w-]+\.(png|webp))$/.exec(ref);
  if (upload) return { abs: join(UPLOADS, upload[1]), file: `${variant === 'day' ? 'bg' : variant}.${upload[2]}`, upload: true };
  const moved = /^\/maps\/([^/]+)\/nodes\/([^/]+)\/([^/]+)$/.exec(ref);
  if (moved) {
    if (decodeURIComponent(moved[1]) !== world) throw new HttpError(422, `${node.id}/${variant}: image from another world`);
    return { abs: join(pack, 'nodes', basename(moved[2]), basename(moved[3])), file: moved[3] };
  }
  return { abs: join(pack, 'nodes', node.id, basename(ref)), file: basename(ref) };
}

function checkProject(project, folders, remove) {
  const errors = [];
  const nodes = Array.isArray(project?.nodes) ? project.nodes : [];
  if (!nodes.length) errors.push('the world must have at least one map');
  const seen = new Set();
  for (const node of nodes) {
    if (!NODE_ID.test(String(node?.id))) errors.push(`invalid id: “${node?.id}”`);
    const key = String(node?.id).toLowerCase();
    if (seen.has(key)) errors.push(`duplicate id: ${node.id}`);
    seen.add(key);
    for (const variant of Object.keys(node?.backgrounds || {})) if (!VARIANT.test(variant)) errors.push(`${node.id}: invalid variant “${variant}”`);
  }
  if (!nodes.some((node) => node.id === project?.startNode)) errors.push(`start node does not exist: ${project?.startNode}`);
  for (const folder of remove) {
    if (!folders.includes(folder)) errors.push(`folder to delete does not exist: ${folder}`);
    if (nodes.some((node) => String(node.id).toLowerCase() === String(folder).toLowerCase())) errors.push(`the folder ${folder} is still in use`);
  }
  if (errors.length) throw new HttpError(422, errors.join(' · '));
}

async function saveWorld(world, { project, revision, remove = [], force = false }) {
  const pack = packOf(world);
  const disk = await snapshot(pack);
  if (!force && revision !== disk.revision) throw new HttpError(409, 'The files on disk changed after loading.');
  if (!Array.isArray(remove)) throw new HttpError(400, 'invalid format');
  checkProject(project, (await readdir(join(pack, 'nodes')).catch(() => [])), remove);

  // Everything is checked and prepared first: if something is wrong, nothing has been written.
  const images = [];
  const texts = [];
  const uploads = [];
  for (const node of project.nodes) {
    const { graph, ...clean } = node;
    clean.backgrounds = {};
    for (const [variant, ref] of Object.entries(node.backgrounds || {})) {
      const source = imageSource(pack, world, node, variant, String(ref));
      if (!existsSync(source.abs)) throw new HttpError(422, `${node.id}/${variant}: image not found (${ref}). Upload it again.`);
      clean.backgrounds[variant] = source.file;
      images.push({ rel: `nodes/${node.id}/${source.file}`, abs: source.abs });
      if (source.upload) uploads.push(source.abs);
    }
    texts.push({ rel: `nodes/${node.id}/node.json`, text: JSON.stringify(clean, null, 2) });
  }
  const previous = parse(disk.manifestText ?? '{}') || {};
  // the title of the game is set in LivingNPC Studio: the one on disk wins over the one the page loaded
  texts.push({ rel: 'project.json', text: JSON.stringify({ ...previous, format: 'livingnpc-map', version: previous.version ?? 1, name: typeof previous.name === 'string' && previous.name.trim() ? previous.name : project.name, startNode: project.startNode, nodes: project.nodes.map((node) => ({ id: node.id, graph: node.graph })) }, null, 2) });
  for (const file of [...images, ...texts]) target(pack, file.rel);

  const written = [];
  let unchanged = 0;
  for (const image of images) {
    const dest = target(pack, image.rel);
    if (same(resolve(image.abs), dest)) { unchanged++; continue; }
    await put(dest, (tmp) => copyFile(image.abs, tmp));
    written.push(image.rel);
  }
  for (const { rel, text } of texts) {   // project.json is last of these: it only ever lists folders that are already complete
    const dest = target(pack, rel);
    if ((await readRaw(dest)) === text) { unchanged++; continue; }
    await put(dest, (tmp) => writeFile(tmp, text, 'utf8'));
    written.push(rel);
  }
  for (const folder of remove) await rm(join(pack, 'nodes', basename(folder)), { recursive: true, force: true });
  // the world is already written: an upload that cannot be removed now only stays in .uploads
  await Promise.all(uploads.map((file) => rm(file, { force: true }).catch(() => {})));
  return { ...(await readWorld(world)), written, unchanged, removed: remove };
}

async function rawBody(req, limit) {
  const chunks = [];
  let size = 0;
  for await (const c of req) {
    size += c.length;
    if (size > limit) throw new HttpError(413, 'request too large');
    chunks.push(c);
  }
  return Buffer.concat(chunks);
}

async function jsonBody(req) {
  try { return JSON.parse((await rawBody(req, MAX_JSON)).toString('utf8')); } catch (e) { throw e instanceof HttpError ? e : new HttpError(400, 'invalid JSON'); }
}

const send = (res, status, data, type = 'application/json; charset=utf-8') => {
  res.writeHead(status, { 'content-type': type, 'cache-control': 'no-store' });
  res.end(typeof data === 'string' || Buffer.isBuffer(data) ? data : JSON.stringify(data));
};

/** Same-machine checks: the Host must be local (DNS rebinding) and writes need a header a foreign page cannot add. */
function guard(req, mutating) {
  const host = String(req.headers.host || '');
  if (!new RegExp(`^(localhost|127\\.0\\.0\\.1):${PORT}$`).test(host)) throw new HttpError(403, 'host not allowed');
  if (!mutating) return;
  if (req.headers['x-livingnpc-map-editor'] !== '1') throw new HttpError(403, 'missing header');
  const origin = req.headers.origin;
  if (origin && origin !== `http://${host}`) throw new HttpError(403, 'origin not allowed');
}

/** The file behind an image URL of the editor (/maps/<world>/nodes/<id>/<file> or /uploads/<file>), or null. */
function imageFile(pathname) {
  const upload = /^\/uploads\/([\w-]+\.(?:png|webp))$/.exec(pathname);
  if (upload) return join(UPLOADS, upload[1]);
  const map = /^\/maps\/([^/]+)\/nodes\/([^/]+)\/([^/]+)$/.exec(pathname);
  if (!map) return null;
  const [world, folder, file] = map.slice(1).map(decodeURIComponent);
  if (!listWorlds().includes(world) || basename(folder) !== folder || basename(file) !== file || !IMAGE_FILE.test(file)) return null;
  return join(WORLDS_DIR, world, PACK, 'nodes', folder, file);
}

async function serveImage(req, res, abs, type) {
  const info = await stat(abs).catch(() => null);
  if (!info?.isFile()) throw new HttpError(404, 'image not found');
  const etag = `"${info.size}-${Math.round(info.mtimeMs)}"`;
  if (req.headers['if-none-match'] === etag) { res.writeHead(304, { etag }); return res.end(); }
  res.writeHead(200, { 'content-type': type, 'content-length': info.size, 'cache-control': 'no-cache', etag });
  createReadStream(abs).pipe(res);
}

/** A small copy for lists and graph, cached on disk: a 1920×1080 PNG is 4 MB to show at 49 px. */
async function serveThumb(req, res, url) {
  const abs = imageFile(url.searchParams.get('src') || '');
  if (!abs) throw new HttpError(404, 'image not found');
  const info = await stat(abs).catch(() => null);
  if (!info?.isFile()) throw new HttpError(404, 'image not found');
  const cached = join(THUMBS, `${createHash('sha1').update(`${abs}|${info.size}|${info.mtimeMs}`).digest('hex')}.webp`);
  if (!existsSync(cached)) {
    try {
      // from a copy in memory: on Windows sharp keeps a file it opened locked, and the upload could not be deleted after saving
      const source = await readFile(abs);
      await put(cached, (tmp) => sharp(source).resize({ width: THUMB_WIDTH, withoutEnlargement: true }).webp({ quality: 72 }).toFile(tmp));
    } catch { return serveImage(req, res, abs, MIME[extname(abs).toLowerCase()]); }
  }
  return serveImage(req, res, cached, MIME['.webp']);
}

async function upload(req, res) {
  const data = await rawBody(req, MAX_IMAGE);
  const png = data.subarray(0, 8).toString('hex') === '89504e470d0a1a0a';
  const webp = data.subarray(0, 4).toString('latin1') === 'RIFF' && data.subarray(8, 12).toString('latin1') === 'WEBP';
  if (!png && !webp) throw new HttpError(400, 'the file is not a PNG or WebP');
  const name = `${randomUUID()}.${png ? 'png' : 'webp'}`;
  await mkdir(UPLOADS, { recursive: true });
  await writeFile(join(UPLOADS, name), data);
  send(res, 200, { url: `/uploads/${name}` });
}

async function api(req, res, url) {
  const world = url.searchParams.get('world') || '';
  if (url.pathname === '/api/worlds') return send(res, 200, { worlds: await Promise.all(listWorlds().map(describeWorld)) });
  if (url.pathname === '/api/world' && req.method === 'GET') return send(res, 200, await readWorld(world));
  if (url.pathname === '/api/upload' && req.method === 'POST') { guard(req, true); return upload(req, res); }
  if (url.pathname === '/api/save' && req.method === 'POST') { guard(req, true); return send(res, 200, await saveWorld(world, await jsonBody(req))); }
  throw new HttpError(404, 'not found');
}

async function serveStatic(res, pathname) {
  const rel = decodeURIComponent(pathname === '/' ? '/index.html' : pathname).replace(/^\/+/, '');
  let abs = resolve(DIST, rel);
  if (relative(DIST, abs).startsWith('..') || !existsSync(abs) || !(await stat(abs)).isFile()) abs = join(DIST, 'index.html');
  if (!existsSync(abs)) throw new HttpError(404, 'Editor not built: run “npm run build”');
  send(res, 200, await readFile(abs), MIME[extname(abs).toLowerCase()] || 'application/octet-stream');
}

createServer(async (req, res) => {
  try {
    const url = new URL(req.url, `http://${req.headers.host || 'localhost'}`);
    guard(req, false);
    if (url.pathname.startsWith('/api/')) await api(req, res, url);
    else if (url.pathname === '/thumb') await serveThumb(req, res, url);
    else if (url.pathname.startsWith('/maps/') || url.pathname.startsWith('/uploads/')) {
      const abs = imageFile(url.pathname);
      if (!abs) throw new HttpError(404, 'image not found');
      await serveImage(req, res, abs, MIME[extname(abs).toLowerCase()]);
    } else await serveStatic(res, url.pathname);
  } catch (e) {
    const status = e instanceof HttpError ? e.status : 500;
    if (status === 500) console.error(e);
    if (!res.headersSent) send(res, status, { error: e.message }); else res.destroy();
  }
}).on('error', (e) => {
  console.error(e.code === 'EADDRINUSE' ? `Port ${PORT} is already in use: the editor may already be open (http://localhost:${PORT}).` : e.message);
  process.exit(1);
}).listen(PORT, '127.0.0.1', () => {
  console.log(`Map Editor: http://localhost:${PORT}`);
  const worlds = listWorlds();
  console.log(worlds.length ? `Worlds: ${worlds.join(', ')} (in ${WORLDS_DIR})` : `WARNING: no world found in ${WORLDS_DIR}. Set the folder with LIVINGNPC_WORLDS.`);
});
