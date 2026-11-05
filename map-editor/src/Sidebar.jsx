import { useMemo, useState } from 'react';
import { Check, Dot, Flag, Map, Plus, Search, X } from 'lucide-react';
import { thumbnailUrl } from './mapUtils';

export default function Sidebar({ project, selectedId, onSelect, onAdd, dirty, world, pack }) {
  const [query, setQuery] = useState('');
  const filtered = useMemo(() => project.nodes.filter((node) => `${node.title} ${node.id}`.toLowerCase().includes(query.toLowerCase())), [project.nodes, query]);
  const invalidLinks = project.nodes.reduce((sum, node) => sum + node.links.filter((link) => !project.nodes.some((n) => n.id === link.target)).length, 0);
  return <aside className="sidebar">
    <div className="sidebar-project" title={pack}><span>WORLD · {world}</span><strong>{project.name}</strong><div><Map size={13} />{project.nodes.length} {project.nodes.length === 1 ? 'place' : 'places'}</div></div>
    <div className="sidebar-search"><Search size={15} /><input placeholder="Search places…" value={query} onChange={(e) => setQuery(e.target.value)} />{query && <button onClick={() => setQuery('')}><X size={13} /></button>}</div>
    <div className="node-list-heading"><span>PLACES</span><button onClick={onAdd} title="New place"><Plus size={15} /></button></div>
    <div className="node-list">
      {filtered.map((node) => <button key={node.id} className={`node-list-item ${selectedId === node.id ? 'selected' : ''}`} onClick={() => onSelect(node.id)}>
        <div className="node-list-image">{thumbnailUrl(node) ? <img src={thumbnailUrl(node)} alt="" loading="lazy" decoding="async" /> : <Map size={19} />}</div>
        <div className="node-list-copy"><strong>{node.title}</strong><span>{node.links.length} {node.links.length === 1 ? 'link' : 'links'}</span></div>
        {project.startNode === node.id && <Flag className="start-flag" size={12} />}
      </button>)}
      {!filtered.length && <div className="search-empty">No places found.</div>}
    </div>
    <button className="add-node" onClick={onAdd}><Plus size={15} />New place</button>
    <div className="sidebar-status"><span className={dirty ? 'saving' : ''}>{dirty ? <Dot size={12} /> : <Check size={12} />}{dirty ? 'Unsaved changes' : 'Saved to disk'}</span><small className={invalidLinks ? 'has-errors' : ''}>{invalidLinks ? `${invalidLinks} missing ${invalidLinks === 1 ? 'destination' : 'destinations'}` : 'Map is valid'}</small></div>
  </aside>;
}
