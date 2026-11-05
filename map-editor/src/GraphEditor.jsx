import { useMemo, useRef, useState } from 'react';
import { LocateFixed, Minus, Plus, Route } from 'lucide-react';
import { thumbnailUrl } from './mapUtils';

const WIDTH = 1560;
const HEIGHT = 960;
const NODE_W = 174;
const NODE_H = 78;
const EDGE_PAD = 500;
const SCROLL_MARGIN = 40;
const SCROLL_STEP = 24;

export default function GraphEditor({ project, selectedId, onSelect, onMove, onArrange }) {
  const [zoom, setZoom] = useState(0.52);
  const drag = useRef(null);
  const viewport = useRef(null);
  const nodesById = useMemo(() => Object.fromEntries(project.nodes.map((node) => [node.id, node])), [project.nodes]);
  const edges = project.nodes.flatMap((node) => node.links.map((link) => ({ ...link, source: node.id }))).filter((edge) => nodesById[edge.target]);
  // The plane grows with the nodes: always keeps free space beyond the farthest one.
  const planeW = Math.max(WIDTH, ...project.nodes.map((node) => node.graph.x + NODE_W / 2 + EDGE_PAD));
  const planeH = Math.max(HEIGHT, ...project.nodes.map((node) => node.graph.y + NODE_H / 2 + EDGE_PAD));

  function pointerDown(event, node) {
    event.preventDefault();
    onSelect(node.id);
    event.currentTarget.setPointerCapture(event.pointerId);
    const view = viewport.current;
    drag.current = { id: node.id, startX: event.clientX, startY: event.clientY, x: node.graph.x, y: node.graph.y, scrollX: view.scrollLeft, scrollY: view.scrollTop };
  }
  function pointerMove(event) {
    if (!drag.current) return;
    const item = drag.current;
    const view = viewport.current;
    // Auto-scroll when the pointer is near the viewport edge.
    const rect = view.getBoundingClientRect();
    if (event.clientX > rect.right - SCROLL_MARGIN) view.scrollLeft += SCROLL_STEP;
    else if (event.clientX < rect.left + SCROLL_MARGIN) view.scrollLeft -= SCROLL_STEP;
    if (event.clientY > rect.bottom - SCROLL_MARGIN) view.scrollTop += SCROLL_STEP;
    else if (event.clientY < rect.top + SCROLL_MARGIN) view.scrollTop -= SCROLL_STEP;
    onMove(item.id, {
      x: Math.max(NODE_W / 2, item.x + (event.clientX - item.startX + view.scrollLeft - item.scrollX) / zoom),
      y: Math.max(NODE_H / 2, item.y + (event.clientY - item.startY + view.scrollTop - item.scrollY) / zoom),
    });
  }
  function edgePath(edge) {
    const a = nodesById[edge.source].graph;
    const b = nodesById[edge.target].graph;
    const reciprocal = nodesById[edge.target].links.some((link) => link.target === edge.source);
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const length = Math.sqrt(dx * dx + dy * dy) || 1;
    const sx = a.x + (dx / length) * 91;
    const sy = a.y + (dy / length) * 48;
    const tx = b.x - (dx / length) * 96;
    const ty = b.y - (dy / length) * 49;
    const bend = reciprocal ? 26 : 0;
    const mx = (sx + tx) / 2 - (dy / length) * bend;
    const my = (sy + ty) / 2 + (dx / length) * bend;
    return `M ${sx} ${sy} Q ${mx} ${my} ${tx} ${ty}`;
  }

  return <section className="graph-workspace">
    <div className="graph-toolbar">
      <div><Route size={15} /><span>{project.nodes.length} {project.nodes.length === 1 ? 'node' : 'nodes'}</span><i /><span>{edges.length} {edges.length === 1 ? 'link' : 'links'}</span></div>
      <button className="text-tool" onClick={onArrange}><LocateFixed size={14} />Auto arrange</button>
    </div>
    <div className="graph-viewport" ref={viewport}>
      <div className="graph-scale" style={{ width: planeW * zoom, height: planeH * zoom }}>
        <div className="graph-plane" style={{ width: planeW, height: planeH, transform: `scale(${zoom})` }}>
          <svg className="graph-edges" width={planeW} height={planeH} viewBox={`0 0 ${planeW} ${planeH}`}>
            <defs><marker id="arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" /></marker></defs>
            {edges.map((edge) => <path key={edge.id} d={edgePath(edge)} className={edge.source === selectedId || edge.target === selectedId ? 'connected' : ''} markerEnd="url(#arrow)" />)}
          </svg>
          {project.nodes.map((node) => <div
            key={node.id}
            className={`graph-node ${selectedId === node.id ? 'selected' : ''} ${project.startNode === node.id ? 'start' : ''}`}
            style={{ left: node.graph.x - NODE_W / 2, top: node.graph.y - NODE_H / 2, width: NODE_W, height: NODE_H }}
            onPointerDown={(event) => pointerDown(event, node)}
            onPointerMove={pointerMove}
            onPointerUp={() => { drag.current = null; }}
            onPointerCancel={() => { drag.current = null; }}
          >
            <img src={thumbnailUrl(node)} alt="" draggable="false" decoding="async" />
            <div><strong>{node.title}</strong><span>{node.id}</span></div>
            {project.startNode === node.id && <b title="Start node">START</b>}
          </div>)}
        </div>
      </div>
    </div>
    <div className="zoom-controls">
      <button onClick={() => setZoom((z) => Math.max(0.2, z - 0.1))} title="Zoom out"><Minus size={16} /></button>
      <span>{Math.round(zoom * 100)}%</span>
      <button onClick={() => setZoom((z) => Math.min(1.3, z + 0.1))} title="Zoom in"><Plus size={16} /></button>
    </div>
    <div className="graph-hint">Drag the nodes to arrange the map · arrows show the direction</div>
  </section>;
}
