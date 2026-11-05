// A small code editor: a textarea for typing, with the colored text drawn behind it, line numbers, marks on the lines
// with problems, auto-indent (Enter, Tab, Shift+Tab). Changes go through insertText so Ctrl+Z keeps working.

const KEYWORDS = new Set(['quest', 'event', 'stage', 'when', 'if', 'elif', 'else', 'choice', 'option', 'pass', 'any', 'all', 'not',
  'autoStart', 'onStart', 'onComplete', 'complete', 'fail', 'onFail']);
const IN = '    ';

const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** One line of script as colored HTML. */
function highlight(line) {
  let out = '', i = 0, first = true;
  const lead = /^\s*/.exec(line)[0];
  out += lead; i = lead.length;
  while (i < line.length) {
    const rest = line.slice(i);
    let m;
    if (rest[0] === '#') { out += `<span class="tk-com">${esc(rest)}</span>`; break; }
    if ((m = /^"(?:[^"\\]|\\.)*"?|^'(?:[^'\\]|\\.)*'?/.exec(rest))) {
      const isWho = first && /^\s*(\[[^\]]*\])?\s*:/.test(rest.slice(m[0].length));
      out += `<span class="${isWho ? 'tk-who' : 'tk-str'}">${esc(m[0])}</span>`;
    } else if ((m = /^\d{1,2}:\d{2}(?!\d)|^-?\d+(\.\d+)?/.exec(rest))) {
      out += `<span class="tk-num">${esc(m[0])}</span>`;
    } else if ((m = /^[A-Za-z_$][A-Za-z0-9_$.\-]*/.exec(rest))) {
      const word = m[0], after = rest.slice(word.length);
      let cls = '';
      if (KEYWORDS.has(word)) cls = 'tk-kw';
      else if (word === 'true' || word === 'false' || word === 'null') cls = 'tk-lit';
      else if (/^\s*\(/.test(after)) cls = 'tk-call';
      else if (first && /^\s*=/.test(after)) cls = 'tk-prop';
      else if (first && /^\s*(\[[^\]]*\])?\s*:\s*\S/.test(after)) cls = 'tk-who';
      out += cls ? `<span class="${cls}">${esc(word)}</span>` : esc(word);
    } else {
      m = [rest[0]];
      out += esc(rest[0]);
    }
    if (!/^\s$/.test(m[0])) first = false;
    i += m[0].length;
  }
  return out;
}

export const indentOf = (line) => /^\s*/.exec(line)[0].replace(/\t/g, IN).length;

export class CodeEditor {
  constructor(root, { onChange }) {
    root.innerHTML = '<div class="gutter-wrap"><pre class="gutter"></pre></div><div class="area"><pre class="hl"></pre><textarea spellcheck="false" wrap="off" autocapitalize="off" autocomplete="off" aria-label="Codice"></textarea></div>';
    this.gutter = root.querySelector('.gutter');
    this.hl = root.querySelector('.hl');
    this.ta = root.querySelector('textarea');
    this.marks = new Map();
    this.onChange = onChange;
    this.ta.addEventListener('input', () => { this.render(); this.onChange?.(); });
    this.ta.addEventListener('scroll', () => this.sync());
    this.ta.addEventListener('keydown', (e) => this.key(e));
    for (const ev of ['click', 'keyup', 'select']) this.ta.addEventListener(ev, () => this.renderCursor());
  }

  get value() { return this.ta.value; }

  /** Replaces the whole text (opening a document): the undo history starts over. */
  set value(text) {
    this.ta.value = text;
    this.ta.setSelectionRange(0, 0);
    this.ta.scrollTop = 0;
    this.render();
  }

  lines() { return this.ta.value.split('\n'); }

  /** The selection, to put it back later (closing a dialog gives the focus back with the cursor at the start). */
  selection() { return [this.ta.selectionStart, this.ta.selectionEnd, this.ta.scrollTop]; }

  restoreSelection([start, end, top]) {
    this.ta.focus();
    this.ta.setSelectionRange(start, end);
    this.ta.scrollTop = top;
    this.render();
  }

  /** 0-based index of the line the cursor is on. */
  cursorLine() { return this.ta.value.slice(0, this.ta.selectionStart).split('\n').length - 1; }

  /** marks: Map(line number (1-based) -> 'err' | 'warn'). */
  setMarks(marks) { this.marks = marks; this.render(); }

  render() {
    const lines = this.lines();
    const cur = this.cursorLine();
    this.hl.innerHTML = lines.map((l, i) => `<span class="line ${this.marks.get(i + 1) || (i === cur ? 'cur' : '')}">${highlight(l) || ' '}</span>`).join('\n') + '\n';
    this.gutter.innerHTML = lines.map((_, i) => { const m = this.marks.get(i + 1); return m ? `<span class="${m}">${i + 1}</span>` : `${i + 1}`; }).join('\n') + '\n';
    this.lastCur = cur;
    this.sync();
  }

  renderCursor() { if (this.cursorLine() !== this.lastCur) this.render(); }

  sync() {
    this.hl.style.transform = `translate(${-this.ta.scrollLeft}px, ${-this.ta.scrollTop}px)`;
    this.gutter.style.transform = `translateY(${-this.ta.scrollTop}px)`;
  }

  /** Puts the cursor at the start of a line (1-based) and scrolls to it. */
  focusLine(n) {
    const lines = this.lines();
    const at = lines.slice(0, Math.max(0, n - 1)).reduce((s, l) => s + l.length + 1, 0) + indentOf(lines[n - 1] || '');
    this.ta.focus();
    this.ta.setSelectionRange(at, at);
    const lh = parseFloat(getComputedStyle(this.ta).lineHeight) || 21;
    this.ta.scrollTop = Math.max(0, (n - 4) * lh);
    this.render();
  }

  /** Replaces the characters [from, to) with text, keeping undo; the cursor ends at caret (absolute) or after the text. */
  edit(from, to, text, caret) {
    this.ta.focus();
    this.ta.setSelectionRange(from, to);
    if (!document.execCommand('insertText', false, text)) this.ta.setRangeText(text, from, to, 'end');
    const at = caret ?? from + text.length;
    this.ta.setSelectionRange(at, at);
    this.render();
    this.onChange?.();
  }

  /** Replaces lines [start, start + count) (0-based) with newLines; the cursor goes to the end of line cursorAt (index into newLines). */
  replaceLines(start, count, newLines, cursorAt = newLines.length - 1) {
    const lines = this.lines();
    const out = [...lines.slice(0, start), ...newLines, ...lines.slice(start + count)];
    // the unchanged lines before and after are left out of the edit, so the undo step is only the new lines
    const all = out.join('\n');
    const from = Math.min(lines.slice(0, start).reduce((s, l) => s + l.length + 1, 0), this.ta.value.length);
    const tail = lines.slice(start + count).join('\n');
    const to = this.ta.value.length - tail.length;
    const text = all.slice(from, all.length - tail.length);
    const caret = out.slice(0, start + cursorAt + 1).join('\n').length;
    const top = this.ta.scrollTop;
    this.edit(from, to, text, caret);
    this.ta.scrollTop = top;
    this.showLine(start + cursorAt + 1);
  }

  /** Scrolls just enough to show line n (1-based). */
  showLine(n) {
    const lh = parseFloat(getComputedStyle(this.ta).lineHeight) || 21;
    const y = (n - 1) * lh, h = this.ta.clientHeight;
    if (y < this.ta.scrollTop + lh) this.ta.scrollTop = Math.max(0, y - 2 * lh);
    else if (y > this.ta.scrollTop + h - 3 * lh) this.ta.scrollTop = y - h + 4 * lh;
    this.sync();
  }

  key(e) {
    const ta = this.ta;
    if (e.key === 'Tab') {
      e.preventDefault();
      const value = ta.value, s = ta.selectionStart, end = ta.selectionEnd;
      const lineStart = value.lastIndexOf('\n', s - 1) + 1;
      if (!e.shiftKey && s === end) { this.edit(s, s, IN); return; }
      const blockEnd = value.indexOf('\n', end - (end > s && value[end - 1] === '\n' ? 1 : 0));
      const stop = blockEnd < 0 ? value.length : blockEnd;
      const block = value.slice(lineStart, stop).split('\n');
      const changed = block.map((l) => (e.shiftKey ? l.replace(/^( {1,4}|\t)/, '') : IN + l)).join('\n');
      this.edit(lineStart, stop, changed);
      ta.setSelectionRange(lineStart, lineStart + changed.length);
      return;
    }
    if (e.key === 'Enter' && !e.ctrlKey && !e.altKey) {
      e.preventDefault();
      const value = ta.value, s = ta.selectionStart;
      const line = value.slice(value.lastIndexOf('\n', s - 1) + 1, s);
      let ind = ' '.repeat(indentOf(line));
      if (line.replace(/#.*$/, '').trimEnd().endsWith(':')) ind += IN;
      this.edit(s, ta.selectionEnd, '\n' + ind);
      return;
    }
    if (e.key === 'Backspace' && ta.selectionStart === ta.selectionEnd) {
      const value = ta.value, s = ta.selectionStart;
      const before = value.slice(value.lastIndexOf('\n', s - 1) + 1, s);
      if (before.length && /^ +$/.test(before)) { e.preventDefault(); this.edit(s - ((before.length - 1) % 4 + 1), s, ''); }
    }
  }
}
