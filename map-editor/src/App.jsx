import { useCallback, useEffect, useRef, useState } from 'react';
import { Download, FolderOpen, Image, LoaderCircle, Play, Save, Workflow } from 'lucide-react';
import Sidebar from './Sidebar';
import SceneEditor from './SceneEditor';
import GraphEditor from './GraphEditor';
import Inspector from './Inspector';
import Player from './Player';
import { AskDialog, ResultBar, WorldDialog } from './Dialogs';
import { clearDraft, downloadBlob, exportProject, fetchWorld, fileUrl, loadDraft, rememberWorld, saveDraft, saveWorld, setActiveWorld, slugify, uid, uploadImage } from './mapUtils';

const mapsWord = (count) => `${count} ${count === 1 ? 'map' : 'maps'}`;

export default function App() {
  const [project, setProject] = useState(null);
  const [selectedId, setSelectedId] = useState(null);
  const [selectedLink, setSelectedLink] = useState(null);
  const [mode, setMode] = useState('scene');
  const [variant, setVariant] = useState('day');
  const [placing, setPlacing] = useState(false);
  const [playing, setPlaying] = useState(false);
  const [toast, setToast] = useState('');
  const [busy, setBusy] = useState('');
  const [result, setResult] = useState(null);
  const [dialog, setDialog] = useState(null);
  const [choosing, setChoosing] = useState(false);
  const [disk, setDisk] = useState(null);   // what the server read: { world, pack, dir, revision, folders }
  const saved = useRef(null);               // the project as it is on disk: if it is another object there are changes
  const saveAction = useRef(null);
  const world = disk?.world;
  const dirty = !!project && project !== saved.current;
  const node = project?.nodes.find((item) => item.id === selectedId) || project?.nodes[0];

  const notify = useCallback((message) => { setToast(message); }, []);
  const fail = useCallback((text) => setResult({ kind: 'error', text }), []);
  const ask = (question) => new Promise((resolve) => setDialog({ ...question, resolve }));
  useEffect(() => { if (!toast) return; const timer = setTimeout(() => setToast(''), 2800); return () => clearTimeout(timer); }, [toast]);

  useEffect(() => {
    if (!project || !world) return;
    if (project === saved.current) { clearDraft(world); return; }
    const timer = setTimeout(() => saveDraft(world, project, disk.revision), 650);
    return () => clearTimeout(timer);
  }, [project, world]);
  useEffect(() => {
    if (!dirty) return;
    const warn = (event) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  function showWorld(data, next, message) {
    setActiveWorld(data.world);
    rememberWorld(data.world);
    window.history.replaceState(null, '', `?world=${encodeURIComponent(data.world)}`);
    saved.current = data.project;
    setDisk(data);
    setProject(next);
    setSelectedId((current) => (next.nodes.some((item) => item.id === current) ? current : next.startNode));
    setSelectedLink(null);
    setChoosing(false);
    setResult({ kind: data.problems.length ? 'warning' : 'success', text: message, details: data.problems });
  }

  async function openWorld(name, discard = false) {
    if (dirty && !discard && !(await ask({
      title: 'Unsaved changes', message: `The changes to “${world}” are not on disk yet and will be lost.`,
      actions: [{ label: 'Cancel', value: false }, { label: 'Discard changes', value: true, danger: true }],
    }))) return;
    if (dirty) clearDraft(world);
    setBusy(`Loading “${name}”…`);
    try {
      const data = await fetchWorld(name);
      setBusy('');
      const draft = loadDraft(name);
      const restore = draft?.project && await ask({
        title: 'Unsaved work found',
        message: `There is a draft of “${name}” from ${new Date(draft.savedAt).toLocaleString('en-GB')} that was never saved.${draft.revision === data.revision ? '' : ' The files on disk have changed since then: restoring it could hide those changes.'}`,
        actions: [{ label: 'Restore the draft', value: true }, { label: 'Use the disk', value: false, primary: true }],
      });
      if (draft && !restore) clearDraft(name);
      setActiveWorld(name);
      showWorld(data, restore ? draft.project : data.project, restore
        ? `Draft restored in “${name}”: remember to save`
        : `Loaded “${name}”: ${mapsWord(data.project.nodes.length)} from ${data.pack}`);
    } catch (error) { fail(`Could not load “${name}”: ${error.message}`); }
    finally { setBusy(''); }
  }

  useEffect(() => {
    const asked = new URLSearchParams(window.location.search).get('world');
    if (asked) openWorld(asked); else setChoosing(true);
  }, []);

  async function doSave(force = false) {
    if (!project || busy) return;
    const live = new Set(project.nodes.map((item) => item.id));
    const removed = disk.folders.filter((folder) => !live.has(folder));
    // The folder of a place whose id changed was only copied: for the game every folder is a map, so it is deleted.
    const sources = project.nodes.flatMap((item) => Object.values(item.backgrounds || {})).join('\n');
    const deleted = removed.filter((folder) => !sources.includes(`/nodes/${folder}/`));
    if (deleted.length && !(await ask({
      title: 'Delete these maps from disk?',
      message: `They are no longer in the world. Saving will permanently delete their folders in ${disk.pack}/nodes. The game reads every folder as a map, so they cannot be left there.`,
      items: deleted,
      actions: [{ label: 'Cancel', value: false }, { label: 'Delete and save', value: true, danger: true }],
    }))) return;
    setBusy(`Saving to ${disk.pack}…`);
    try {
      const data = await saveWorld(world, { project, revision: disk.revision, remove: removed, force });
      const parts = [`${data.written.length} ${data.written.length === 1 ? 'file written' : 'files written'}`, `${data.unchanged} unchanged`];
      if (data.removed.length) parts.push(`${data.removed.length} ${data.removed.length === 1 ? 'folder deleted' : 'folders deleted'}`);
      clearDraft(world);
      showWorld(data, data.project, `Saved to ${data.pack}: ${parts.join(', ')}`);
    } catch (error) {
      setBusy('');
      if (error.status !== 409) return fail(`Save failed: ${error.message}`);
      const answer = await ask({
        title: 'The files on disk have changed',
        message: `Something changed ${disk.pack} after it was loaded. Overwriting will lose those changes; reloading will lose the ones made here.`,
        actions: [{ label: 'Cancel', value: '' }, { label: 'Reload from disk', value: 'reload', primary: true }, { label: 'Overwrite', value: 'force', danger: true }],
      });
      if (answer === 'force') return doSave(true);
      if (answer === 'reload') await openWorld(world, true);
    } finally { setBusy(''); }
  }
  saveAction.current = doSave;

  const selectNode = useCallback((id) => { setSelectedId(id); setSelectedLink(null); setPlacing(false); }, []);
  const updateNode = (patch) => setProject((current) => ({ ...current, nodes: current.nodes.map((item) => item.id === node.id ? { ...item, ...patch } : item) }));
  const updateLink = (id, patch) => updateNode({ links: node.links.map((item) => item.id === id ? { ...item, ...patch } : item) });
  const deleteLink = useCallback((id) => {
    setProject((current) => ({ ...current, nodes: current.nodes.map((item) => item.id === selectedId ? { ...item, links: item.links.filter((link) => link.id !== id) } : item) }));
    setSelectedLink(null);
  }, [selectedId]);

  useEffect(() => {
    function keydown(event) {
      if (event.key === 'Escape') { setPlacing(false); setSelectedLink(null); }
      if ((event.key === 'Delete' || event.key === 'Backspace') && selectedLink && !['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement.tagName)) deleteLink(selectedLink);
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') { event.preventDefault(); saveAction.current(); }
    }
    window.addEventListener('keydown', keydown);
    return () => window.removeEventListener('keydown', keydown);
  }, [selectedLink, deleteLink]);

  function addNode() {
    const base = slugify(`new_place_${project.nodes.length + 1}`);
    let id = base; let index = 2;
    while (project.nodes.some((item) => item.id === id)) id = `${base}_${index++}`;
    const reference = node?.graph || { x: 400, y: 300 };
    const newNode = { id, title: 'New place', text: '', backgrounds: {}, links: [], graph: { x: reference.x + 100, y: reference.y + 100 } };
    setProject((current) => ({ ...current, nodes: [...current.nodes, newNode] }));
    selectNode(id); setMode('scene');
    notify('Place created: now upload its background');
  }
  // A place just created has a temporary id (new_place_N, or nuovo_ambiente_N from older versions): when it gets a title
  // the id becomes the title as a slug + number (e.g. "Library" → library_1), updating links and the start node.
  // Ids already chosen (alley, castle_entrance…) are never touched: other files of the pack may refer to them.
  function renameNodeFromTitle() {
    if (!/^(new_place|nuovo_ambiente)_\d+(_\d+)?$/.test(node.id)) return;
    const base = slugify(node.title);
    if (base === 'new_node') return;
    let index = 1;
    while (project.nodes.some((item) => item.id === `${base}_${index}`)) index++;
    const oldId = node.id; const newId = `${base}_${index}`;
    setProject((current) => ({
      ...current,
      startNode: current.startNode === oldId ? newId : current.startNode,
      nodes: current.nodes.map((item) => {
        const links = item.links.map((link) => link.target === oldId ? { ...link, target: newId } : link);
        if (item.id !== oldId) return { ...item, links };
        const backgrounds = Object.fromEntries(Object.entries(item.backgrounds || {}).map(([key, file]) => [key, fileUrl(oldId, file)]));
        return { ...item, id: newId, links, backgrounds };
      }),
    }));
    setSelectedId(newId);
    notify(`ID updated: ${newId}`);
  }
  function deleteNode() {
    if (project.nodes.length === 1) return notify('The map must contain at least one node.');
    if (!window.confirm(`Delete “${node.title}” and every link to this node?`)) return;
    const remaining = project.nodes.filter((item) => item.id !== node.id).map((item) => ({ ...item, links: item.links.filter((link) => link.target !== node.id) }));
    const nextStart = project.startNode === node.id ? remaining[0].id : project.startNode;
    setProject({ ...project, nodes: remaining, startNode: nextStart });
    selectNode(remaining[0].id); notify('Node deleted');
  }
  function addLink(x = 50, y = 50) {
    const target = project.nodes.find((item) => item.id !== node.id);
    if (!target) return notify('Create a second place first.');
    const id = uid('link');
    updateNode({ links: [...node.links, { id, target: target.id, label: target.title, x, y, width: 20, height: 6.5 }] });
    setSelectedLink(id); setPlacing(false);
    notify('Link added');
  }
  function moveGraphNode(id, graph) { setProject((current) => ({ ...current, nodes: current.nodes.map((item) => item.id === id ? { ...item, graph } : item) })); }
  function arrangeGraph() {
    setProject((current) => ({ ...current, nodes: current.nodes.map((item, index) => ({ ...item, graph: { x: 170 + (index % 5) * 295, y: 130 + Math.floor(index / 5) * 190 } })) }));
    notify('Nodes arranged on a grid');
  }
  async function uploadBackground(key, file) {
    const nodeId = node.id;
    setBusy('Uploading the image…');
    try {
      const url = await uploadImage(file);
      setProject((current) => ({ ...current, nodes: current.nodes.map((item) => item.id === nodeId ? { ...item, backgrounds: { ...item.backgrounds, [key]: url } } : item) }));
      setResult({ kind: 'success', text: `Image “${file.name}” uploaded to ${nodeId}: press Save World to write it to disk` });
    } catch (error) { fail(`Image not uploaded: ${error.message}`); }
    finally { setBusy(''); }
  }
  async function doExport() {
    setBusy('Preparing the ZIP…');
    try {
      const blob = await exportProject(project);
      downloadBlob(blob, `${slugify(project.name)}_livingnpc-map.zip`);
      notify('ZIP copy downloaded');
    } catch (error) { fail(`Export failed: ${error.message}`); }
    finally { setBusy(''); }
  }

  const overlays = <>
    {choosing && <WorldDialog current={world} onLoad={(name) => openWorld(name)} onClose={project ? () => setChoosing(false) : null} />}
    {dialog && <AskDialog {...dialog} onAnswer={(value) => { setDialog(null); dialog.resolve(value); }} />}
    {busy && <div className="busy-overlay"><LoaderCircle className="spin" size={24} /><span>{busy}</span></div>}
  </>;

  if (!project || !node) {
    return <div className="loading-screen">
      <span className="brand-mark"><i /><i /><i /></span>
      {result?.kind === 'error' && <><p><b>World not loaded</b></p><small>{result.text}</small><button className="world-button" onClick={() => setChoosing(true)}><FolderOpen size={15} />Load World</button></>}
      {overlays}
    </div>;
  }

  return <div className="app-shell">
    <header className="app-header">
      <div className="brand"><span className="brand-mark"><i /><i /><i /></span><div><strong>LIVINGNPC</strong><span>MAP EDITOR</span></div></div>
      <nav className="mode-tabs" aria-label="Editor mode">
        <button className={mode === 'scene' ? 'active' : ''} onClick={() => setMode('scene')}><Image size={15} />Scene</button>
        <button className={mode === 'graph' ? 'active' : ''} onClick={() => { setMode('graph'); setPlacing(false); }}><Workflow size={15} />Graph</button>
      </nav>
      <div className="header-actions">
        <button className="world-button" onClick={() => setChoosing(true)}><FolderOpen size={15} /><span>Load World</span></button>
        <button className={`world-button save ${dirty ? 'dirty' : ''}`} onClick={() => doSave()} title={`Save to ${disk.pack} (Ctrl+S)`}><Save size={15} /><span>Save World</span></button>
        <button className="export-button" onClick={doExport} title="Download a ZIP copy of the world"><Download size={15} /><span>Export ZIP</span></button>
        <button className="play-button" onClick={() => setPlaying(true)}><Play size={15} fill="currentColor" /><span>Try the map</span></button>
      </div>
    </header>
    {result && <ResultBar result={result} onClose={() => setResult(null)} />}
    <div className="editor-body">
      <Sidebar project={project} selectedId={node.id} onSelect={selectNode} onAdd={addNode} dirty={dirty} world={world} pack={disk.pack} />
      <main className="main-workspace">
        <div className="workspace-heading"><div><span>{mode === 'scene' ? 'SCENE EDITOR' : 'GRAPH VIEW'}</span><h1>{mode === 'scene' ? node.title : project.name}</h1></div>{mode === 'scene' && <code>nodes/{node.id}/</code>}</div>
        {mode === 'scene' ? <SceneEditor node={node} nodes={project.nodes} variant={variant} onVariant={setVariant} selectedLink={selectedLink} onSelectLink={setSelectedLink} onMoveLink={(id, x, y) => updateLink(id, { x, y })} onResizeLink={(id, width) => updateLink(id, { width })} onAddLink={addLink} placing={placing} onPlace={() => { setPlacing(true); setSelectedLink(null); }} onCancelPlace={() => setPlacing(false)} /> : <GraphEditor project={project} selectedId={node.id} onSelect={selectNode} onMove={moveGraphNode} onArrange={arrangeGraph} />}
      </main>
      <Inspector node={node} nodes={project.nodes} project={project} selectedLink={selectedLink} onSelectLink={setSelectedLink} onUpdateNode={updateNode} onUpdateLink={updateLink} onDeleteLink={deleteLink} onAddLink={() => setPlacing(true)} onUpload={uploadBackground} onRemoveVariant={(key) => { const backgrounds = { ...node.backgrounds }; delete backgrounds[key]; updateNode({ backgrounds }); if (variant === key) setVariant('day'); }} onDeleteNode={deleteNode} onTitleCommit={renameNodeFromTitle} onSetStart={() => { setProject((current) => ({ ...current, startNode: node.id })); notify('Start node updated'); }} notify={notify} />
    </div>
    {playing && <Player project={project} initialNode={project.startNode} initialVariant={variant} onClose={() => setPlaying(false)} />}
    {toast && <div className="toast">{toast}</div>}
    {overlays}
  </div>;
}
