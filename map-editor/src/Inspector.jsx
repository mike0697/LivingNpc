import { useRef, useState } from 'react';
import { Braces, Check, ChevronRight, Copy, Download, Flag, ImagePlus, Link2, MapPin, Plus, Trash2, Upload, X } from 'lucide-react';
import { backgroundFileName, downloadBlob, nodeForExport, thumbnailUrl, variantLabel } from './mapUtils';

function Field({ label, children, hint }) {
  return <label className="field"><span>{label}{hint && <small>{hint}</small>}</span>{children}</label>;
}

/** The node's tags with one added or removed; no tags at all is stored as "no field" so untagged nodes keep their file as it was. */
function withTag(tags, tag, on) {
  const rest = (tags || []).filter((t) => t !== tag);
  const next = on ? [...rest, tag] : rest;
  return next.length ? next : undefined;
}

export default function Inspector({ node, nodes, project, selectedLink, onSelectLink, onUpdateNode, onUpdateLink, onDeleteLink, onAddLink, onUpload, onRemoveVariant, onDeleteNode, onTitleCommit, onSetStart, notify }) {
  const [tab, setTab] = useState('node');
  const [uploadKey, setUploadKey] = useState('day');
  const filePicker = useRef(null);
  const copy = async () => { await navigator.clipboard.writeText(JSON.stringify(nodeForExport(node), null, 2)); notify('Node JSON copied'); };
  const download = () => downloadBlob(new Blob([JSON.stringify(nodeForExport(node), null, 2)], { type: 'application/json' }), `${node.id}.json`);
  function chooseVariant(key) {
    setUploadKey(key);
    setTimeout(() => filePicker.current?.click(), 0);
  }
  function chooseCustomVariant() {
    const key = window.prompt('Variant name (e.g. evening, rain):');
    if (key?.trim()) chooseVariant(key.trim().toLowerCase().replace(/\s+/g, '_').replace(/[^a-z0-9_]/g, ''));
  }
  function uploadPickedFile(event) {
    const file = event.target.files?.[0];
    if (file && uploadKey) onUpload(uploadKey, file);
    event.target.value = '';
  }

  return <aside className="inspector">
    <input ref={filePicker} type="file" accept="image/png,image/jpeg,image/webp" onChange={uploadPickedFile} hidden />
    <div className="inspector-tabs"><button className={tab === 'node' ? 'active' : ''} onClick={() => setTab('node')}>Properties</button><button className={tab === 'json' ? 'active' : ''} onClick={() => setTab('json')}><Braces size={13} />JSON</button></div>
    {tab === 'json' ? <div className="json-panel">
      <div className="json-actions"><span>nodes/{node.id}/node.json</span><div><button onClick={copy} title="Copy JSON"><Copy size={14} /></button><button onClick={download} title="Download JSON"><Download size={14} /></button></div></div>
      <pre>{JSON.stringify(nodeForExport(node), null, 2)}</pre>
      <p>Coordinates are percentages, measured from the top-left corner.</p>
    </div> : <div className="inspector-scroll">
      <section className="property-section node-properties">
        <div className="section-heading"><span>NODE</span>{project.startNode === node.id && <em><Flag size={10} />Start</em>}</div>
        <Field label="Title"><input value={node.title} onChange={(e) => onUpdateNode({ title: e.target.value })} onBlur={onTitleCommit} onKeyDown={(e) => { if (e.key === 'Enter') e.currentTarget.blur(); }} /></Field>
        <Field label="Narrative text" hint={`${node.text.length} characters`}><textarea rows="4" value={node.text} onChange={(e) => onUpdateNode({ text: e.target.value })} placeholder="Describe what the player sees…" /></Field>
        <label className="check-row"><input type="checkbox" checked={(node.tags || []).includes('bed')} onChange={(e) => onUpdateNode({ tags: withTag(node.tags, 'bed', e.target.checked) })} /><span>Bed<small>the player can sleep here (tag “bed”)</small></span></label>
        <div className="node-id"><span>ID / folder</span><code>{node.id}</code></div>
      </section>

      <section className="property-section">
        <div className="section-heading"><span>BACKGROUNDS</span><button className="mini-action" onClick={chooseCustomVariant} title="Add a custom variant"><Plus size={13} /></button></div>
        <div className="background-list">
          {Object.keys(node.backgrounds || {}).map((variant) => <BackgroundRow key={variant} node={node} variant={variant} onUpload={onUpload} onRemove={onRemoveVariant} />)}
          {!Object.keys(node.backgrounds || {}).length && <div className="no-background"><ImagePlus size={22} /><span>No image uploaded</span></div>}
        </div>
        <label className="upload-variant"><Plus size={13} />Add / replace variant<select value="" onChange={(e) => { if (e.target.value) chooseVariant(e.target.value); }}><option value="" disabled>Choose variant…</option><option value="day">Day · bg</option><option value="afternoon">Afternoon</option><option value="night">Night</option><option value="night_light">Night with lights</option></select></label>
      </section>

      <section className="property-section links-section">
        <div className="section-heading"><span>LINKS <b>{node.links.length}</b></span><button className="mini-action" onClick={onAddLink}><Plus size={13} /></button></div>
        <div className="link-list">
          {node.links.map((item, index) => <div className={`link-item ${selectedLink === item.id ? 'open' : ''}`} key={item.id}>
            <button className="link-summary" onClick={() => onSelectLink(selectedLink === item.id ? null : item.id)}><span className="link-number">{index + 1}</span><span>{item.label || 'No label'}<small>→ {nodes.find((n) => n.id === item.target)?.title || 'Missing destination'}</small></span><ChevronRight size={14} /></button>
            {selectedLink === item.id && <div className="link-fields">
              <Field label="Button label"><input value={item.label} onChange={(e) => onUpdateLink(item.id, { label: e.target.value })} /></Field>
              <Field label="Destination"><select value={item.target} onChange={(e) => onUpdateLink(item.id, { target: e.target.value })}><option value="">Select node…</option>{nodes.filter((n) => n.id !== node.id).map((n) => <option key={n.id} value={n.id}>{n.title}</option>)}</select></Field>
              <Field label="Travel time (minutes)" hint="empty = game default"><input type="number" min="1" max="1440" step="1" placeholder="default" value={item.minutes ?? ''} onChange={(e) => onUpdateLink(item.id, { minutes: e.target.value === '' ? undefined : Math.max(1, Math.round(Number(e.target.value))) })} /></Field>
              <div className="state-grid">
                <label className="check-row"><input type="checkbox" checked={item.visible !== false} onChange={(e) => onUpdateLink(item.id, { visible: e.target.checked ? undefined : false })} /><span>Visible at start<small>shown on the map</small></span></label>
                <label className="check-row"><input type="checkbox" checked={item.open !== false} onChange={(e) => onUpdateLink(item.id, { open: e.target.checked ? undefined : false })} /><span>Open at start<small>can be crossed</small></span></label>
              </div>
              <Field label="Message when locked" hint="lockedText">{item.lockedText && typeof item.lockedText !== 'string'
                ? <input disabled value="Multilingual text: edit the JSON" />
                : <input value={item.lockedText || ''} placeholder="e.g. The door is locked." onChange={(e) => onUpdateLink(item.id, { lockedText: e.target.value || undefined })} />}</Field>
              <div className="coordinate-grid"><Field label="X %"><input type="number" min="0" max="100" step="0.1" value={Math.round(item.x * 10) / 10} onChange={(e) => onUpdateLink(item.id, { x: Number(e.target.value) })} /></Field><Field label="Y %"><input type="number" min="0" max="100" step="0.1" value={Math.round(item.y * 10) / 10} onChange={(e) => onUpdateLink(item.id, { y: Number(e.target.value) })} /></Field><Field label="Width %"><input type="number" min="8" max="80" step="0.5" value={Math.round((item.width || 18) * 10) / 10} onChange={(e) => onUpdateLink(item.id, { width: Number(e.target.value) })} /></Field><Field label="Height %"><input type="number" min="3" max="30" step="0.5" value={Math.round((item.height || 6.5) * 10) / 10} onChange={(e) => onUpdateLink(item.id, { height: Number(e.target.value) })} /></Field></div>
              <button className="delete-link" onClick={() => onDeleteLink(item.id)}><Trash2 size={13} />Delete link</button>
            </div>}
          </div>)}
          {!node.links.length && <div className="empty-links"><Link2 size={22} /><span>No links</span><button onClick={onAddLink}><Plus size={13} />Add the first one</button></div>}
        </div>
      </section>
      <section className="property-section node-footer-actions">
        {project.startNode !== node.id && <button onClick={onSetStart}><Flag size={14} />Set as start node</button>}
        <button className="danger" onClick={onDeleteNode}><Trash2 size={14} />Delete node</button>
      </section>
    </div>}
  </aside>;
}

function BackgroundRow({ node, variant, onUpload, onRemove }) {
  const input = useRef(null);
  function change(event) {
    const file = event.target.files?.[0];
    if (!file) return;
    onUpload(variant, file);
    event.target.value = '';
  }
  return <div className="background-row">
    <div className="background-thumb">{thumbnailUrl(node, variant) ? <img src={thumbnailUrl(node, variant)} alt="" decoding="async" /> : <ImagePlus size={18} />}</div>
    <div><strong>{variantLabel(variant)}</strong><span>{backgroundFileName(variant, node.backgrounds?.[variant])}</span></div>
    <input ref={input} id={`bg-${variant}`} type="file" accept="image/png,image/jpeg,image/webp" onChange={change} hidden />
    <button onClick={() => input.current.click()} title="Upload or replace"><Upload size={14} /></button>
    {variant !== 'day' && <button className="remove-bg" onClick={() => onRemove(variant)} title="Remove variant"><X size={13} /></button>}
  </div>;
}
