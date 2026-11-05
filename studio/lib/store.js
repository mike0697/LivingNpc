// Reading and writing a pack on disk (Node only): the list of documents, their files, the ids the buttons offer.

import { readdir, readFile, writeFile, mkdir, rm, rmdir, rename } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { join, dirname, relative, sep } from 'node:path';

const rel = (pack, abs) => relative(pack, abs).split(sep).join('/');

async function walk(dir, filter) {
  if (!existsSync(dir)) return [];
  const out = [];
  for (const e of await readdir(dir, { withFileTypes: true })) {
    const abs = join(dir, e.name);
    if (e.isDirectory()) out.push(...await walk(abs, filter));
    else if (!filter || filter(e.name)) out.push(abs);
  }
  return out.sort();
}

const isJson = (n) => n.toLowerCase().endsWith('.json');

async function readJson(abs) {
  try { return JSON.parse((await readFile(abs, 'utf8')).replace(/^﻿/, '')); } catch { return null; }
}

const textOf = (v) => (typeof v === 'string' ? v : v && typeof v === 'object' ? (v.it ?? Object.values(v)[0] ?? '') : '');

/** The documents of a pack: [{ id, kind: 'quest' | 'folder' | 'event', title, files: [rel], eventDir }]. */
export async function listDocs(pack) {
  const docs = [];
  const questIds = new Set();
  for (const abs of await walk(join(pack, 'quests'), isJson)) {
    const json = await readJson(abs);
    const id = json?.id || abs.split(sep).pop().replace(/\.json$/i, '');
    questIds.add(id);
    const events = await walk(join(pack, 'events', id), isJson);
    docs.push({ id: `quest:${id}`, kind: 'quest', title: textOf(json?.title) || id, name: id, files: [rel(pack, abs), ...events.map((e) => rel(pack, e))], eventDir: `events/${id}` });
  }
  const eventsDir = join(pack, 'events');
  if (existsSync(eventsDir)) {
    for (const e of (await readdir(eventsDir, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name))) {
      if (e.isDirectory()) {
        if (questIds.has(e.name)) continue;
        const files = await walk(join(eventsDir, e.name), isJson);
        if (files.length) docs.push({ id: `folder:${e.name}`, kind: 'folder', title: e.name, name: e.name, files: files.map((f) => rel(pack, f)), eventDir: `events/${e.name}` });
      } else if (isJson(e.name)) {
        const id = e.name.replace(/\.json$/i, '');
        docs.push({ id: `event:${id}`, kind: 'event', title: id, name: id, files: [`events/${e.name}`], eventDir: 'events' });
      }
    }
  }
  return docs;
}

/** The files of a document with their JSON: [{ rel, json }] (a file that is not valid JSON makes the whole read fail). */
export async function readDoc(pack, doc) {
  const out = [];
  for (const r of doc.files) {
    const json = await readJson(join(pack, r));
    if (json === null) throw new Error(`${r} is not valid JSON`);
    out.push({ rel: r, json });
  }
  return out;
}

/** Only the JSON files the program edits (quests/, events/, npcs/, schedules/, items/, rules.json, title.json), never above the pack. */
export function safePath(pack, r) {
  const ok = typeof r === 'string' && (r === 'rules.json' || r === 'title.json' || /^(quests|events|npcs|schedules|items)\/[A-Za-z0-9_.\-/]+\.json$/.test(r));
  if (!ok || r.split('/').includes('..')) throw new Error(`path not allowed: ${r}`);
  return join(pack, ...r.split('/'));
}

const IMAGE_RE = /\.(png|jpe?g|webp)$/i;
const SOUND_RE = /\.(mp3|ogg|wav)$/i;
const cleanRel = (r) => typeof r === 'string' && /^[^\\:*?"<>|]+$/.test(r) && !r.split('/').some((p) => p === '..' || p === '.' || !p);

/** A picture the page may show: images/… or nodes/<id>/… (png, jpg or webp), never above the pack. */
export function imagePath(pack, r) {
  if (!cleanRel(r) || !/^(images|nodes)\//.test(r) || !IMAGE_RE.test(r)) throw new Error(`image not allowed: ${r}`);
  return join(pack, ...r.split('/'));
}

/** A sound the page may play: music/… or sfx/… (mp3, ogg, wav), never above the pack. */
export function soundPath(pack, r) {
  if (!cleanRel(r) || !/^(music|sfx)\//.test(r) || !SOUND_RE.test(r)) throw new Error(`sound not allowed: ${r}`);
  return join(pack, ...r.split('/'));
}

/** Writes a new file in images/ (a picture) or music/ (a sound), base64 content. Refuses to replace a file that is already there. */
export async function saveImage(pack, r, base64) {
  let abs;
  if (r.startsWith('images/')) abs = imagePath(pack, r);
  else if (r.startsWith('music/')) abs = soundPath(pack, r);
  else throw new Error('new files go in the images folder (pictures) or the music folder (music)');
  if (existsSync(abs)) throw new Error(`${r} already exists`);
  await mkdir(dirname(abs), { recursive: true });
  await writeFile(abs, Buffer.from(String(base64), 'base64'));
  return r;
}

/**
 * The records of a pack: { npc: [{ rel, json, text }], schedule: [...], item: [...], rules: { rel, json, text } | null,
 * title: the same for title.json, nodes: [{ id, title, backgrounds: { key: 'nodes/<dir>/<file>' } }], startNode,
 * worldName (name of project.json) }. json is null when the file is not valid JSON.
 */
export async function listRecords(pack) {
  const read = async (dir) => Promise.all((await walk(join(pack, dir), isJson)).map(async (abs) => {
    const text = await readFile(abs, 'utf8');
    let json = null;
    try { json = JSON.parse(text.replace(/^﻿/, '')); } catch { /* shown as a broken file */ }
    return { rel: rel(pack, abs), json, text };
  }));
  const single = async (name) => {
    const abs = join(pack, name);
    if (!existsSync(abs)) return null;
    const text = await readFile(abs, 'utf8');
    let json = null;
    try { json = JSON.parse(text.replace(/^﻿/, '')); } catch { /* broken */ }
    return { rel: name, json, text };
  };
  const rules = await single('rules.json');
  const title = await single('title.json');
  const nodes = [];
  for (const abs of await walk(join(pack, 'nodes'), (n) => n === 'node.json')) {
    const j = await readJson(abs);
    if (!j?.id) continue;
    const dir = rel(pack, dirname(abs));
    const backgrounds = {};
    for (const [k, f] of Object.entries(j.backgrounds || {})) if (typeof f === 'string') backgrounds[k] = `${dir}/${f}`;
    nodes.push({ id: j.id, title: textOf(j.title) || j.id, backgrounds });
  }
  const project = (await readJson(join(pack, 'project.json'))) || {};
  return {
    npc: await read('npcs'), schedule: await read('schedules'), item: await read('items'), rules, title,
    nodes: nodes.sort((a, b) => a.id.localeCompare(b.id)),
    startNode: project.startNode || rules?.json?.startNode || '',
    worldName: typeof project.name === 'string' ? project.name : '',
  };
}

/**
 * Changes only "name" of project.json (the title of the game in the menu); the rest of the file belongs to the map
 * editor and is written back as it was. The folder of the world does not change: saves of the game are kept by folder.
 */
export async function setWorldName(pack, name) {
  const clean = typeof name === 'string' ? name.trim() : '';
  if (!clean) throw new Error('write a title');
  if (clean.length > 80 || /[\r\n\t]/.test(clean)) throw new Error('the title must fit on one line (80 characters at most)');
  const abs = join(pack, 'project.json');
  if (!existsSync(abs)) throw new Error('project.json is missing: open and save the world once in the map editor');
  const project = await readJson(abs);
  if (!project || typeof project !== 'object' || Array.isArray(project)) throw new Error('project.json is not valid JSON');
  project.name = clean;
  const tmp = `${abs}.tmp`;
  await writeFile(tmp, JSON.stringify(project, null, 2));
  await rename(tmp, abs);
  return clean;
}

/** Every JSON file that can use an id (for "where is it used"): [{ rel, json }]. */
export async function allJson(pack) {
  const out = [];
  for (const dir of ['quests', 'events', 'npcs', 'schedules', 'items']) for (const abs of await walk(join(pack, dir), isJson)) {
    const json = await readJson(abs);
    if (json !== null) out.push({ rel: rel(pack, abs), json });
  }
  const rules = await readJson(join(pack, 'rules.json'));
  if (rules) out.push({ rel: 'rules.json', json: rules });
  return out;
}

/** Writes (through a temp file) and removes files. Unchanged files are left alone. Returns the paths actually changed. */
export async function saveFiles(pack, write, remove) {
  const targets = Object.entries(write).map(([r, text]) => [safePath(pack, r), r, text]);
  const gone = remove.map((r) => [safePath(pack, r), r]);
  const changed = [];
  for (const [abs, r, text] of targets) {
    if (existsSync(abs) && (await readFile(abs, 'utf8')) === text) continue;
    await mkdir(dirname(abs), { recursive: true });
    await writeFile(abs + '.tmp', text, 'utf8');
    await rename(abs + '.tmp', abs);
    changed.push(r);
  }
  for (const [abs, r] of gone) {
    if (!existsSync(abs)) continue;
    await rm(abs);
    changed.push(r);
    // the folder of a quest's messages goes with its last file
    const dir = dirname(abs);
    if (r.startsWith('events/') && r.split('/').length > 2 && existsSync(dir) && !(await readdir(dir)).length) await rmdir(dir).catch(() => {});
  }
  return changed;
}

/** The ids of the pack, for the buttons and the checks: { npc, node, link, item, quest, stage: {quest: [...]}, event, stat, activity, flag, images, sfx, music }. */
export async function refs(pack) {
  const ids = async (dir) => (await Promise.all((await walk(join(pack, dir), isJson)).map(readJson))).filter(Boolean);
  const files = async (dir) => (await walk(join(pack, dir))).map((f) => relative(join(pack, dir), f).split(sep).join('/'));
  const nodes = [];
  for (const abs of await walk(join(pack, 'nodes'), (n) => n === 'node.json')) { const j = await readJson(abs); if (j) nodes.push(j); }
  const quests = await ids('quests');
  const events = await ids('events');
  const rules = (await readJson(join(pack, 'rules.json'))) || {};
  const flags = new Set();
  const scan = (v) => {
    if (Array.isArray(v)) return v.forEach(scan);
    if (!v || typeof v !== 'object') return;
    if ((v.do === 'setFlag' || v.do === 'addFlag' || v.type === 'flag') && typeof v.key === 'string') flags.add(v.key);
    Object.values(v).forEach(scan);
  };
  quests.forEach(scan); events.forEach(scan);
  const sorted = (a) => [...new Set(a.filter(Boolean))].sort();
  return {
    npc: sorted((await ids('npcs')).map((n) => n.id)),
    node: sorted(nodes.map((n) => n.id)),
    link: sorted(nodes.flatMap((n) => (n.links || []).map((l) => l.id))),
    item: sorted((await ids('items')).map((i) => i.id)),
    schedule: sorted((await ids('schedules')).map((s) => s.id)),
    quest: sorted(quests.map((q) => q.id)),
    stage: Object.fromEntries(quests.map((q) => [q.id, (q.stages || []).map((s, i) => s.id || `stage${i}`)])),
    event: sorted(events.map((e) => e.id)),
    stat: sorted((rules.stats || []).map((s) => s.id)),
    activity: sorted((rules.activities || []).map((a) => (typeof a === 'string' ? a : a.id))),
    flag: sorted([...flags]),
    images: await files('images'),
    sfx: await files('sfx'),
    music: await files('music'),
  };
}
