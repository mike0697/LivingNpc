// LivingNPC Studio: the program serves its page and reads / writes the quests, events, npcs, schedules, items and
// rules.json, title.json, hud.json and actions.json of a world, the title of the game in project.json (and new pictures in images/, new music in music/).
// node server.mjs  (or start.bat) -> http://127.0.0.1:5180
// The worlds are in ../worlds (env LIVINGNPC_WORLDS to use another folder, e.g. a copy for tests); each world's files are
// in <world>/base. Only 127.0.0.1 is served, and writes need the header the page sends.

import { createServer } from 'node:http';
import { readFile, readdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { join, dirname, extname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { listDocs, readDoc, saveFiles, refs, listRecords, allJson, imagePath, soundPath, saveImage, setWorldName } from './lib/store.js';
import { usages } from './lib/records.js';

const here = dirname(fileURLToPath(import.meta.url));
const WORLDS = resolve(process.env.LIVINGNPC_WORLDS || join(here, '..', 'worlds'));
const PORT = Number(process.env.PORT || 5180);
const WRITE_HEADER = 'x-livingnpc-studio';

class HttpError extends Error { constructor(status, message) { super(message); this.status = status; } }

async function worldNames() {
  if (!existsSync(WORLDS)) return [];
  return (await readdir(WORLDS, { withFileTypes: true }))
    .filter((e) => e.isDirectory() && existsSync(join(WORLDS, e.name, 'base'))).map((e) => e.name).sort();
}

async function packOf(world) {
  if (!(await worldNames()).includes(world)) throw new HttpError(404, `unknown world: ${world}`);
  return join(WORLDS, world, 'base');
}

async function body(req) {
  const chunks = [];
  let size = 0;
  for await (const c of req) {
    size += c.length;
    if (size > 30 * 1024 * 1024) throw new HttpError(413, 'request too large');
    chunks.push(c);
  }
  try { return JSON.parse(Buffer.concat(chunks).toString('utf8') || '{}'); } catch { throw new HttpError(400, 'invalid request'); }
}

const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.webp': 'image/webp', '.mp3': 'audio/mpeg', '.ogg': 'audio/ogg', '.wav': 'audio/wav' };

async function serveFile(res, base, path) {
  const abs = resolve(base, '.' + path);
  if (!abs.startsWith(base) || !existsSync(abs)) throw new HttpError(404, 'not found');
  res.writeHead(200, { 'content-type': TYPES[extname(abs)] || 'application/octet-stream', 'cache-control': 'no-store' });
  res.end(await readFile(abs));
}

function send(res, status, data) {
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
  res.end(JSON.stringify(data));
}

async function handle(req, res) {
  const url = new URL(req.url, 'http://localhost');
  const p = url.pathname;
  const world = url.searchParams.get('world');

  if (p.startsWith('/api/')) {
    if (req.method === 'POST' && req.headers[WRITE_HEADER] !== '1') throw new HttpError(403, 'write not allowed');
    if (p === '/api/worlds') return send(res, 200, { worlds: await worldNames(), folder: WORLDS });
    const pack = await packOf(world);
    if (p === '/api/docs') return send(res, 200, { docs: await listDocs(pack) });
    if (p === '/api/refs') return send(res, 200, await refs(pack));
    if (p === '/api/doc') {
      const doc = (await listDocs(pack)).find((d) => d.id === url.searchParams.get('id'));
      if (!doc) throw new HttpError(404, 'document not found');
      return send(res, 200, { doc, files: await readDoc(pack, doc) });
    }
    if (p === '/api/save' && req.method === 'POST') {
      const { write = {}, remove = [] } = await body(req);
      let changed;
      try { changed = await saveFiles(pack, write, remove); } catch (e) { throw new HttpError(400, e.message); }
      return send(res, 200, { changed, docs: await listDocs(pack) });
    }
    if (p === '/api/records') return send(res, 200, await listRecords(pack));
    if (p === '/api/world-name' && req.method === 'POST') {
      const { name } = await body(req);
      try { return send(res, 200, { name: await setWorldName(pack, name) }); } catch (e) { throw new HttpError(400, e.message); }
    }
    if (p === '/api/usages') return send(res, 200, { usages: usages(url.searchParams.get('kind'), url.searchParams.get('id'), await allJson(pack)) });
    if (p === '/api/image') {
      let abs;
      try { abs = imagePath(pack, url.searchParams.get('path')); } catch (e) { throw new HttpError(400, e.message); }
      if (!existsSync(abs)) throw new HttpError(404, 'image not found');
      res.writeHead(200, { 'content-type': TYPES[extname(abs).toLowerCase()] || 'application/octet-stream', 'cache-control': 'no-store' });
      return res.end(await readFile(abs));
    }
    if (p === '/api/audio') {
      let abs;
      try { abs = soundPath(pack, url.searchParams.get('path')); } catch (e) { throw new HttpError(400, e.message); }
      if (!existsSync(abs)) throw new HttpError(404, 'sound not found');
      res.writeHead(200, { 'content-type': TYPES[extname(abs).toLowerCase()] || 'application/octet-stream', 'cache-control': 'no-store' });
      return res.end(await readFile(abs));
    }
    if (p === '/api/upload' && req.method === 'POST') {
      const { path, data } = await body(req);
      // the answer is the name as the files use it: relative to images/ or music/
      try { return send(res, 200, { path: (await saveImage(pack, path, data)).replace(/^(images|music)\//, '') }); } catch (e) { throw new HttpError(400, e.message); }
    }
    throw new HttpError(404, 'api sconosciuta');
  }
  if (p === '/' || p === '/index.html') return serveFile(res, join(here, 'public'), '/index.html');
  if (p.startsWith('/lib/')) return serveFile(res, join(here, 'lib'), p.slice(4));
  return serveFile(res, join(here, 'public'), p);
}

createServer((req, res) => {
  handle(req, res).catch((e) => send(res, e.status || 500, { error: e.message }));
}).listen(PORT, '127.0.0.1', () => {
  console.log(`LivingNPC Studio: http://127.0.0.1:${PORT}  (worlds in ${WORLDS})`);
});
