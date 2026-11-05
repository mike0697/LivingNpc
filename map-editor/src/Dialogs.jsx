import { useEffect, useState } from 'react';
import { AlertTriangle, ChevronRight, FolderOpen, LoaderCircle, Map, RotateCw, X } from 'lucide-react';
import { fetchWorlds, lastWorld } from './mapUtils';

/** Picking the world: a click on a row loads it from disk. */
export function WorldDialog({ current, onLoad, onClose }) {
  const [worlds, setWorlds] = useState(null);
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    setError('');
    fetchWorlds().then(setWorlds).catch((failure) => setError(failure.message));
  }, [attempt]);
  const recent = lastWorld();
  const sorted = [...(worlds || [])].sort((a, b) => (b.name === recent) - (a.name === recent));

  return <div className="dialog-backdrop">
    <section className="dialog world-dialog" role="dialog" aria-label="Load World">
      <header><div><strong>Load World</strong><span>Pick the world to open: it is read from disk.</span></div>{onClose && <button className="dialog-close" onClick={onClose} title="Close"><X size={16} /></button>}</header>
      {error && <div className="dialog-error"><p>{error}</p><button onClick={() => setAttempt((n) => n + 1)}>Retry</button></div>}
      {!worlds && !error && <div className="dialog-loading"><LoaderCircle className="spin" size={18} />Looking for worlds…</div>}
      {worlds && !worlds.length && <div className="dialog-error"><p>No world found in the worlds folder.</p></div>}
      <div className="world-list">
        {sorted.map((world) => <button key={world.name} className="world-row" disabled={!!world.error} onClick={() => onLoad(world.name)}>
          {world.error ? <AlertTriangle size={18} /> : world.name === current ? <RotateCw size={18} /> : <FolderOpen size={18} />}
          <span className="world-row-copy">
            <strong>{world.name}</strong>
            <small>{world.error || `${world.maps} ${world.maps === 1 ? 'map' : 'maps'} · ${world.pack}`}{world.misplaced > 0 && ` · ${world.misplaced} misplaced ${world.misplaced === 1 ? 'file' : 'files'}`}</small>
          </span>
          {!world.error && <em>{world.name === current ? 'Reload from disk' : 'Load'}<ChevronRight size={13} /></em>}
        </button>)}
      </div>
    </section>
  </div>;
}

/** A question with its buttons: `actions` is [{ label, value, danger?, primary? }]. */
export function AskDialog({ title, message, items, actions, onAnswer }) {
  return <div className="dialog-backdrop">
    <section className="dialog ask-dialog" role="alertdialog" aria-label={title}>
      <header><div><strong>{title}</strong></div></header>
      <p>{message}</p>
      {items?.length > 0 && <ul>{items.map((item) => <li key={item}><Map size={12} />{item}</li>)}</ul>}
      <footer>{actions.map((action) => <button key={action.label} className={action.danger ? 'danger' : action.primary ? 'primary' : ''} onClick={() => onAnswer(action.value)}>{action.label}</button>)}</footer>
    </section>
  </div>;
}

/** The outcome of the last operation: it stays visible until closed, unlike the short messages. */
export function ResultBar({ result, onClose }) {
  const [open, setOpen] = useState(false);
  useEffect(() => setOpen(false), [result]);
  return <div className={`result-bar ${result.kind}`} role="status">
    <div className="result-line">
      <span>{result.text}</span>
      {result.details?.length > 0 && <button onClick={() => setOpen((value) => !value)}>{open ? 'Hide' : `Show ${result.details.length} ${result.details.length === 1 ? 'warning' : 'warnings'}`}</button>}
      <button className="result-close" onClick={onClose} title="Close"><X size={14} /></button>
    </div>
    {open && <ul>{result.details.map((item) => <li key={item}>{item}</li>)}</ul>}
  </div>;
}
