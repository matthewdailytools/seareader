import { loadPageTurning, pageKeyDelta } from './page-turning';
import { useTranslation } from './locale';
import { OcrPanel } from './ocr-panel';
import { speechParts, useListening } from './listening';
import { Annotations, type Annotation, type TextSelection } from './annotations';
import type { Appearance } from './appearance';
import { useEffect, useMemo, useRef, useState } from 'react';
import { ReaderSearch, searchLimit, textExcerpt, type SearchHit } from './reader-search';

export function TextReader({ preview, bytes, id, initialPosition, appearance, title }: { preview?: boolean; bytes: Uint8Array; id: string; initialPosition?: string; appearance: Appearance; title: string }) {
  const tr = useTranslation();
  const startListening = useListening();
  const [saved, setSaved] = useState<{ encoding: string; offset: number }>();
  const [encoding, setEncoding] = useState('');
  const [paged, setPaged] = useState(() => localStorage.getItem('txt-reading-mode') === 'page');
  const [scrollLimits, setScrollLimits] = useState({ start: true, end: true });
  const [error, setError] = useState('');
  const [selection, setSelection] = useState<TextSelection>();
  const [annotations, setAnnotations] = useState<Annotation[]>([]);
  const viewport = useRef<HTMLDivElement>(null);
  const offset = useRef(0);
  const previous = useRef('');
  const lines = useMemo(() => {
    if (!encoding) return [];
    const text = new TextDecoder(encoding).decode(bytes);
    let offset = 0;
    return text.split('\n').map(text => {
      const line = { text, offset };
      offset += text.length + 1;
      return line;
    });
  }, [bytes, encoding]);

  useEffect(() => {
    let active = true;
    Promise.resolve(initialPosition ?? (preview ? null : window.seaReader.getPosition(id))).then(raw => {
      if (!active) return;
      let initialEncoding = bytes[0] === 255 && bytes[1] === 254 ? 'utf-16le'
        : bytes[0] === 254 && bytes[1] === 255 ? 'utf-16be' : 'utf-8';
      if (raw) {
        try {
          const position = JSON.parse(raw);
          new TextDecoder(position.encoding);
          if (typeof position.encoding === 'string' && Number.isSafeInteger(position.offset) && position.offset >= 0) {
            setSaved(position);
            initialEncoding = position.encoding;
          }
        } catch { /* An unreadable position can be recovered from the start. */ }
      }
      setEncoding(initialEncoding);
    }, () => {
      if (!active) return;
      setError('无法读取保存的阅读位置。');
      setEncoding('utf-8');
    });
    return () => { active = false; };
  }, [bytes, id, initialPosition]);

  useEffect(() => {
    const element = viewport.current;
    if (!element || !encoding) return;
    offset.current = saved?.encoding === encoding ? saved.offset : 0;
    const line = lines.findLast(line => line.offset <= offset.current);
    if (line) {
      const target = element.querySelector<HTMLElement>('[data-offset="' + line.offset + '"]');
      element.scrollTop = target?.offsetTop ?? 0;
    }
    function persist() {
      const position = JSON.stringify({ encoding, offset: offset.current });
      if (position === previous.current) return;
      previous.current = position;
      if (!preview) void window.seaReader.savePosition(id, position).catch(() => {
        if (previous.current === position) previous.current = '';
      });
    }
    let timer: ReturnType<typeof setTimeout> | undefined;
    function updateLimits() {
      if (!element) return;
      setScrollLimits({
        start: element.scrollTop < 1,
        end: element.scrollTop + element.clientHeight >= element.scrollHeight - 1
      });
    }
    function onScroll() {
      updateLimits();
      if (!element) return;
      const children = element.children as HTMLCollectionOf<HTMLElement>;
      let low = 0;
      let high = children.length - 1;
      while (low < high) {
        const middle = Math.ceil((low + high) / 2);
        if (children[middle].offsetTop <= element.scrollTop) low = middle;
        else high = middle - 1;
      }
      offset.current = Number(children[low]?.dataset.offset ?? 0);
      clearTimeout(timer);
      timer = setTimeout(persist, 250);
    }
    if (initialPosition) persist();
    updateLimits();
    const observer = new ResizeObserver(updateLimits);
    observer.observe(element);
    element.addEventListener('scroll', onScroll, { passive: true });
    return () => {
      clearTimeout(timer);
      observer.disconnect();
      element.removeEventListener('scroll', onScroll);
      persist();
    };
  }, [encoding, id, initialPosition, lines, saved]);

  useEffect(() => {
    let active = true;
    if (!preview) void window.seaReader.listAnnotations(id).then(items => {
      if (active) setAnnotations(items);
    }).catch(() => {
      if (active) setError('无法读取标注。');
    });
    return () => { active = false; };
  }, [id]);

  useEffect(() => {
    const element = viewport.current;
    if (!element || !encoding) return;
    const ranges: Range[] = [];
    for (const item of annotations) {
      try {
        const position = JSON.parse(item.position);
        if (position.encoding !== encoding || !Number.isSafeInteger(position.offset) ||
            !Number.isSafeInteger(position.end) || position.end <= position.offset) continue;
        const first = lines.findLast(line => line.offset <= position.offset);
        const last = lines.findLast(line => line.offset <= position.end);
        if (!first || !last) continue;
        const startNode = element.querySelector('[data-offset="' + first.offset + '"]')?.firstChild;
        const endNode = element.querySelector('[data-offset="' + last.offset + '"]')?.firstChild;
        if (!startNode || !endNode) continue;
        const range = document.createRange();
        range.setStart(startNode, position.offset - first.offset);
        range.setEnd(endNode, position.end - last.offset);
        ranges.push(range);
      } catch { /* Ignore a stale or malformed annotation position. */ }
    }
    CSS.highlights.set('sea-txt-annotations', new Highlight(...ranges));
    return () => { CSS.highlights.delete('sea-txt-annotations'); };
  }, [annotations, encoding, lines]);

  function captureSelection() {
    const selection = window.getSelection();
    const element = viewport.current;
    if (!element || !selection?.rangeCount || selection.isCollapsed) return;
    const range = selection.getRangeAt(0);
    if (!element.contains(range.startContainer) || !element.contains(range.endContainer)) return;
    function textOffset(node: Node, index: number) {
      const line = node.parentElement?.closest<HTMLElement>('[data-offset]');
      if (node.nodeType !== Node.TEXT_NODE || !line || line.parentElement !== element) return;
      return Number(line.dataset.offset) + index;
    }
    const offset = textOffset(range.startContainer, range.startOffset);
    const end = textOffset(range.endContainer, range.endOffset);
    const quotation = selection.toString().trim().slice(0, 12000);
    if (offset === undefined || end === undefined || end <= offset || !quotation) return;
    setSelection({ position: JSON.stringify({ encoding, offset, end }), quotation });
  }

  function turnPage(direction: number) {
    const element = viewport.current;
    if (!element) return;
    const lineHeight = appearance.size * appearance.lineHeight;
    element.scrollTop += direction * Math.max(lineHeight, Math.floor(element.clientHeight / lineHeight) * lineHeight);
  }

  function jumpToLine(target: number) {
    const element = viewport.current;
    const line = element?.querySelector<HTMLElement>('[data-offset="' + target + '"]');
    if (element && line) element.scrollTop = line.offsetTop;
  }

  function listen(from?: number) {
    const parts: string[] = [];
    let startIndex: number | undefined;
    for (const line of lines) {
      let cursor = 0;
      for (const part of speechParts(line.text)) {
        const at = line.text.indexOf(part, cursor);
        cursor = at + part.length;
        if (from !== undefined && startIndex === undefined && from < line.offset + cursor) {
          startIndex = parts.length;
          parts.push(part.slice(Math.max(0, from - line.offset - at)));
        } else {
          parts.push(part);
        }
      }
    }
    if (from === undefined || startIndex !== undefined) {
      startListening({ id: preview ? '' : 'txt:' + id + ':' + encoding, title, parts }, startIndex);
    }
  }

  function listenFromSelection(paragraph: boolean) {
    if (!selection) return;
    const position = JSON.parse(selection.position);
    const start = paragraph ? lines.findLast(line => line.offset <= position.offset)?.offset : position.offset;
    if (start !== undefined) listen(start);
  }

  if (!encoding) return <p>{tr('正在读取…', 'Loading…', '読み込み中…')}</p>;
  return <>
    {error && <p role="alert">{error}</p>}
    <label>{tr('文本编码', 'Text encoding', '文字コード')} <select value={encoding} onChange={event => { setSaved(undefined); setSelection(undefined); setEncoding(event.target.value); }}>
      <option value="utf-8">UTF-8</option><option value="gb18030">GB18030</option>
      <option value="shift_jis">Shift_JIS</option><option value="utf-16le">UTF-16 LE</option>
      <option value="utf-16be">UTF-16 BE</option>
    </select></label>
    <div className="reader-controls">
      <button disabled={!lines.some(line => line.text.trim())} onClick={() => listen()}>{tr('听书', 'Listen', '読み上げ')}</button>
      {selection && <>
        <button onClick={() => listenFromSelection(false)}>{tr('从这里听起', 'Read from here', 'ここから読み上げ')}</button>
        <button onClick={() => listenFromSelection(true)}>{tr('从本段听起', 'Read from this paragraph', 'この段落から読み上げ')}</button>
      </>}
    </div>
    <OcrPanel selectedText={selection?.quotation} />
    {!preview && <Annotations bookId={id} selection={selection} items={annotations} onChange={setAnnotations} onJump={target => {
      try {
        const position = JSON.parse(target);
        if (position.encoding !== encoding) {
          new TextDecoder(position.encoding);
          setSelection(undefined);
          setSaved(position);
          setEncoding(position.encoding);
        } else {
          const line = lines.findLast(line => line.offset <= position.offset);
          if (line) jumpToLine(line.offset);
        }
      } catch {
        setError('无法跳转到此标注。');
      }
    }} />}
    <div className="reader-controls">
      <label>{tr('阅读方式', 'Reading mode', '閲覧モード')} <select value={paged ? 'page' : 'scroll'} onChange={event => {
        setPaged(event.target.value === 'page');
        localStorage.setItem('txt-reading-mode', event.target.value);
      }}>
        <option value="page">{tr('左右翻页', 'Pages', 'ページ送り')}</option>
        <option value="scroll">{tr('上下滚动', 'Continuous scroll', '連続スクロール')}</option>
      </select></label>
      {paged && <>
        <button disabled={scrollLimits.start} onClick={() => turnPage(-1)}>{tr('上一页', 'Previous page', '前のページ')}</button>
        <button disabled={scrollLimits.end} onClick={() => turnPage(1)}>{tr('下一页', 'Next page', '次のページ')}</button>
      </>}
    </div>
    <ReaderSearch onSelect={jumpToLine} search={async query => {
      const results: SearchHit<number>[] = [];
      for (const line of lines) {
        const match = line.text.toLowerCase().indexOf(query.toLowerCase());
        if (match >= 0) results.push({ target: line.offset, excerpt: textExcerpt(line.text, match, query.length) });
        if (results.length >= searchLimit) break;
      }
      return results;
    }} />
    <article className={paged ? 'txt-view txt-paged' : 'txt-view'} style={{
      fontFamily: appearance.font, fontSize: appearance.size,
      lineHeight: appearance.lineHeight, paddingInline: appearance.margin
    }} tabIndex={0} aria-label={tr('TXT 正文', 'TXT text', 'TXT 本文')} ref={viewport} onPointerUp={captureSelection} onKeyUp={captureSelection} onKeyDown={event => {
      const direction = paged ? pageKeyDelta(event.nativeEvent, loadPageTurning()) : 0;
      if (direction) { event.preventDefault(); turnPage(direction); }
    }}>
      {lines.map(line => <div data-offset={line.offset} key={line.offset}>{line.text || '\u00a0'}</div>)}
    </article>
  </>;
}
