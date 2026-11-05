import JSZip from 'jszip';

export const VARIANTS = [
  { id: 'day', label: 'Day', short: 'Day' },
  { id: 'afternoon', label: 'Afternoon', short: 'Afternoon' },
  { id: 'night', label: 'Night', short: 'Night' },
  { id: 'night_light', label: 'Night with lights', short: 'Night + lights' },
];

export const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
export const uid = (prefix = 'item') => `${prefix}_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 6)}`;
export const slugify = (text) => text.toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, '') || 'new_node';

let activeWorld = '';
export const setActiveWorld = (world) => { activeWorld = world; };

// An image reference is a file name in the node's folder or an already complete URL:
// /uploads/… (uploaded, not saved yet) or /maps/… (in another node folder, after an id change).
export function fileUrl(nodeId, file) {
  return file.startsWith('/') ? file : `/maps/${encodeURIComponent(activeWorld)}/nodes/${nodeId}/${file}`;
}

export function backgroundUrl(node, variant = 'day') {
  const file = node.backgrounds?.[variant] || node.backgrounds?.day;
  return file ? fileUrl(node.id, file) : '';
}

/** The small copy of the background, for lists and the graph. */
export function thumbnailUrl(node, variant = 'day') {
  const url = backgroundUrl(node, variant);
  return url ? `/thumb?src=${encodeURIComponent(url)}` : '';
}

export function variantLabel(key) {
  return VARIANTS.find((item) => item.id === key)?.label || key.replaceAll('_', ' ');
}

/** The file name a background gets in the node's folder: a fresh upload is named after its variant (bg.png, night.webp…). */
export function backgroundFileName(variant, source) {
  if (!source) return `${variant === 'day' ? 'bg' : variant}.png`;
  const upload = /^\/uploads\/[\w-]+\.(png|webp)$/.exec(source);
  return upload ? `${variant === 'day' ? 'bg' : variant}.${upload[1]}` : source.split('/').pop();
}

/** PNG and WebP go up as they are (the game reads both); anything else (JPG…) is turned into PNG. */
async function toUploadable(file) {
  if (file.type === 'image/png' || file.type === 'image/webp') return file;
  const bitmap = await createImageBitmap(file);
  const canvas = document.createElement('canvas');
  canvas.width = bitmap.width;
  canvas.height = bitmap.height;
  canvas.getContext('2d').drawImage(bitmap, 0, 0);
  return new Promise((resolve, reject) => canvas.toBlob((blob) => (blob ? resolve(blob) : reject(new Error('PNG conversion failed'))), 'image/png'));
}

export function downloadBlob(blob, filename) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = filename;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1500);
}

export function nodeForExport(node) {
  const { graph, ...data } = node;
  return { ...data, backgrounds: { ...node.backgrounds }, links: node.links.map((link) => ({ ...link })) };
}

export async function exportProject(project) {
  const zip = new JSZip();
  zip.file('project.json', JSON.stringify({
    format: 'livingnpc-map', version: 1, name: project.name, startNode: project.startNode,
    nodes: project.nodes.map((node) => ({ id: node.id, graph: node.graph })),
  }, null, 2));
  for (const node of project.nodes) {
    const clean = nodeForExport(node);
    for (const [variant, source] of Object.entries(node.backgrounds || {})) {
      const response = await fetch(fileUrl(node.id, source));
      if (!response.ok) throw new Error(`Image not reachable: ${node.id}/${variant}`);
      const filename = backgroundFileName(variant, source);
      clean.backgrounds[variant] = filename;
      zip.file(`nodes/${node.id}/${filename}`, await response.arrayBuffer());
    }
    zip.file(`nodes/${node.id}/node.json`, JSON.stringify(clean, null, 2));
  }
  return zip.generateAsync({ type: 'blob', compression: 'DEFLATE', compressionOptions: { level: 4 } });
}

async function request(url, options) {
  const response = await fetch(url, options);
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw Object.assign(new Error(data.error || `The server is not responding (${response.status}): is the Map Editor window still open?`), { status: response.status });
  return data;
}

const WRITE = { 'x-livingnpc-map-editor': '1' };

export const fetchWorlds = async () => (await request('/api/worlds')).worlds;
export const fetchWorld = (world) => request(`/api/world?world=${encodeURIComponent(world)}`);

export function saveWorld(world, body) {
  return request(`/api/save?world=${encodeURIComponent(world)}`, { method: 'POST', headers: { ...WRITE, 'content-type': 'application/json' }, body: JSON.stringify(body) });
}

/** Sends the image to the server, which keeps it aside until saving; the project only keeps its URL. */
export async function uploadImage(file) {
  return (await request('/api/upload', { method: 'POST', headers: WRITE, body: await toUploadable(file) })).url;
}

// The draft only keeps work from being lost if the page closes before saving: it is never applied on its own.
const draftKey = (world) => `livingnpc-map-editor:draft:${world}`;

export function saveDraft(world, project, revision) {
  try { localStorage.setItem(draftKey(world), JSON.stringify({ project, revision, savedAt: Date.now() })); } catch {}
}
export function loadDraft(world) {
  try { return JSON.parse(localStorage.getItem(draftKey(world))); } catch { return null; }
}
export function clearDraft(world) {
  try { localStorage.removeItem(draftKey(world)); } catch {}
}
export function rememberWorld(world) {
  try { localStorage.setItem('livingnpc-map-editor:last-world', world); } catch {}
}
export function lastWorld() {
  try { return localStorage.getItem('livingnpc-map-editor:last-world'); } catch { return null; }
}
