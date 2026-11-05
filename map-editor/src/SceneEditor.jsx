import { useRef, useState } from 'react';
import { Crosshair, ImageOff, MousePointer2, Plus } from 'lucide-react';
import { backgroundUrl, clamp, VARIANTS, variantLabel } from './mapUtils';

export default function SceneEditor({ node, nodes, variant, onVariant, selectedLink, onSelectLink, onMoveLink, onResizeLink, onAddLink, placing, onPlace, onCancelPlace }) {
  const stage = useRef(null);
  const drag = useRef(null);
  const [pointer, setPointer] = useState(null);
  const variants = VARIANTS.filter((item) => node.backgrounds?.[item.id]);
  const extra = Object.keys(node.backgrounds || {}).filter((key) => !VARIANTS.some((item) => item.id === key));
  const activeVariant = node.backgrounds?.[variant] ? variant : 'day';

  function pointFromEvent(event) {
    const rect = stage.current.getBoundingClientRect();
    return {
      x: clamp(((event.clientX - rect.left) / rect.width) * 100, 0, 100),
      y: clamp(((event.clientY - rect.top) / rect.height) * 100, 0, 100),
    };
  }
  function startDrag(event, link, mode = 'move') {
    if (placing) return;
    event.preventDefault();
    event.stopPropagation();
    onSelectLink(link.id);
    event.currentTarget.setPointerCapture(event.pointerId);
    drag.current = { id: link.id, mode };
  }
  function moveDrag(event) {
    if (!drag.current) return;
    const point = pointFromEvent(event);
    if (drag.current.mode === 'resize') onResizeLink(drag.current.id, Math.max(8, (point.x - (node.links.find((l) => l.id === drag.current.id)?.x || 0)) * 2));
    else onMoveLink(drag.current.id, point.x, point.y);
  }
  function endDrag() { drag.current = null; }
  function stageClick(event) {
    if (!placing || event.target.closest('.hotspot')) return;
    const point = pointFromEvent(event);
    onAddLink(point.x, point.y);
  }

  return <section className="scene-workspace">
    <div className="scene-toolbar">
      <div className="variant-tabs" aria-label="Scene variant">
        {variants.map((item) => <button key={item.id} className={activeVariant === item.id ? 'active' : ''} onClick={() => onVariant(item.id)}>{item.short}</button>)}
        {extra.map((key) => <button key={key} className={activeVariant === key ? 'active' : ''} onClick={() => onVariant(key)}>{variantLabel(key)}</button>)}
      </div>
      <button className={`tool-button ${placing ? 'placing' : ''}`} onClick={placing ? onCancelPlace : onPlace} title="Add a link and click on the image">
        {placing ? <Crosshair size={15} /> : <Plus size={15} />}{placing ? 'Click on the scene…' : 'Link'}
      </button>
    </div>
    <div className={`scene-canvas ${placing ? 'is-placing' : ''}`}>
      <div className="scene-frame">
        <div className="scene-stage" ref={stage} onClick={stageClick} onPointerMove={(event) => { moveDrag(event); if (placing && event.target === event.currentTarget) setPointer(pointFromEvent(event)); }} onPointerLeave={() => setPointer(null)}>
          {backgroundUrl(node, activeVariant) ? <img src={backgroundUrl(node, activeVariant)} alt={node.title} draggable="false" /> : <div className="empty-scene"><ImageOff size={30} /><strong>No background</strong><span>Upload a background from the node panel</span></div>}
          {node.links.map((link, index) => {
            const target = nodes.find((item) => item.id === link.target);
            return <button
              key={link.id}
              className={`hotspot editor-hotspot ${selectedLink === link.id ? 'selected' : ''} ${link.visible === false ? 'is-hidden' : ''} ${link.open === false ? 'is-closed' : ''}`}
              style={{ left: `${link.x}%`, top: `${link.y}%`, width: `${link.width || 18}%`, height: `${link.height || 6.5}%` }}
              onPointerDown={(event) => startDrag(event, link)}
              onPointerMove={moveDrag}
              onPointerUp={endDrag}
              onPointerCancel={endDrag}
              onClick={(event) => { event.stopPropagation(); onSelectLink(link.id); }}
              title={target ? `Go to ${target.title}` : 'Missing destination'}
            >
              <span className="hotspot-index">{String(index + 1).padStart(2, '0')}</span>
              <span>{link.label || target?.title || 'No destination'}</span>
              {link.visible === false && <em className="state-badge">hidden</em>}
              {link.open === false && <em className="state-badge">locked</em>}
              {selectedLink === link.id && <i className="resize-handle" onPointerDown={(event) => startDrag(event, link, 'resize')} onPointerMove={moveDrag} onPointerUp={endDrag} />}
            </button>;
          })}
          {placing && pointer && <div className="placement-reticle" style={{ left: `${pointer.x}%`, top: `${pointer.y}%` }}><Crosshair size={26} /></div>}
        </div>
      </div>
      <div className="canvas-hint"><MousePointer2 size={13} /><span>Drag the buttons to place them · coordinates in % from the top-left corner</span></div>
    </div>
  </section>;
}
