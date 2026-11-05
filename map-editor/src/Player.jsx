import { useEffect, useMemo, useState } from 'react';
import { ArrowLeft, Flag, Home, Lock, Moon, RotateCcw, Sun, X } from 'lucide-react';
import { backgroundUrl, VARIANTS, variantLabel } from './mapUtils';

export default function Player({ project, initialNode, initialVariant, onClose }) {
  const [currentId, setCurrentId] = useState(initialNode || project.startNode);
  const [history, setHistory] = useState([]);
  const [variant, setVariant] = useState(initialVariant);
  const [overrides, setOverrides] = useState({});   // link id -> { visible, open }: simulates showLink / openLink effects
  const [toast, setToast] = useState('');
  const node = project.nodes.find((item) => item.id === currentId) || project.nodes[0];
  const variants = useMemo(() => Object.keys(node.backgrounds || {}), [node]);
  const activeVariant = node.backgrounds?.[variant] ? variant : 'day';

  useEffect(() => {
    function keydown(event) { if (event.key === 'Escape') onClose(); }
    window.addEventListener('keydown', keydown);
    return () => window.removeEventListener('keydown', keydown);
  }, [onClose]);

  function go(target) {
    if (!project.nodes.some((item) => item.id === target)) return;
    setHistory((items) => [...items, currentId]);
    setCurrentId(target);
  }
  function back() {
    if (!history.length) return;
    setCurrentId(history.at(-1));
    setHistory((items) => items.slice(0, -1));
  }
  function restart() { setCurrentId(project.startNode); setHistory([]); setOverrides({}); }
  const linkState = (link) => ({ visible: overrides[link.id]?.visible ?? link.visible !== false, open: overrides[link.id]?.open ?? link.open !== false });
  const setLinkState = (id, patch) => setOverrides((current) => ({ ...current, [id]: { ...current[id], ...patch } }));
  function press(link) {
    if (!linkState(link).open) {
      setToast(typeof link.lockedText === 'string' && link.lockedText ? link.lockedText : 'It is locked.');
      setTimeout(() => setToast(''), 2500);
      return;
    }
    go(link.target);
  }

  return <div className="player" role="dialog" aria-label="Playable preview">
    <header className="player-header">
      <div className="player-brand"><span className="brand-mark small"><i /><i /><i /></span><div><strong>{project.name}</strong><span>MAP PREVIEW</span></div></div>
      <div className="player-actions">
        <button onClick={back} disabled={!history.length}><ArrowLeft size={16} />Back</button>
        <button onClick={restart}><RotateCcw size={15} />Restart</button>
        <button className="player-close" onClick={onClose}><X size={18} />Exit</button>
      </div>
    </header>
    <main className="player-scene">
      <div className="player-stage">
        <img key={`${node.id}-${activeVariant}`} className="player-bg" src={backgroundUrl(node, activeVariant)} alt={node.title} />
        <div className="player-vignette" />
        <div className="player-location"><span>CURRENT NODE</span><strong>{node.title}</strong>{node.id === project.startNode && <em><Flag size={11} />Start</em>}</div>
        {node.links.filter((link) => linkState(link).visible).map((link) => <button key={link.id} className={`hotspot player-hotspot ${linkState(link).open ? '' : 'is-closed'}`} style={{ left: `${link.x}%`, top: `${link.y}%`, width: `${link.width || 18}%`, height: `${link.height || 6.5}%` }} onClick={() => press(link)}><span>{link.label || project.nodes.find((n) => n.id === link.target)?.title}</span>{linkState(link).open ? <ArrowLeft size={14} /> : <Lock size={13} />}</button>)}
        {toast && <div className="player-toast">{toast}</div>}
        {node.links.length > 0 && <aside className="player-state">
          <strong>Try the effects</strong>
          {node.links.map((link) => <div className="player-state-row" key={link.id}>
            <span>{link.label || link.id}</span>
            <label><input type="checkbox" checked={linkState(link).visible} onChange={(e) => setLinkState(link.id, { visible: e.target.checked })} />visible</label>
            <label><input type="checkbox" checked={linkState(link).open} onChange={(e) => setLinkState(link.id, { open: e.target.checked })} />open</label>
          </div>)}
        </aside>}
        <div className="narrative-panel"><div className="narrative-rule" /><p>{node.text}</p></div>
      </div>
    </main>
    <footer className="player-footer">
      <div className="player-path"><Home size={13} /><span>{project.nodes.find((n) => n.id === project.startNode)?.title}</span>{history.length > 0 && <><b>›</b><span>{node.title}</span></>}</div>
      <div className="player-variants"><span>{activeVariant.includes('night') ? <Moon size={13} /> : <Sun size={13} />}Time of day</span>{variants.map((key) => <button key={key} className={activeVariant === key ? 'active' : ''} onClick={() => setVariant(key)}>{variantLabel(key)}</button>)}</div>
    </footer>
  </div>;
}
