import { savePreference } from './preferences';
import { loadPageTurning, pageKeyDelta } from './page-turning';
import { useTranslation } from './locale';
import { OcrPanel } from './ocr-panel';
import { speechParts, useListening } from './listening';
import { Annotations, type Annotation, type TextSelection } from './annotations';
import type { Appearance } from './appearance';
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type MouseEvent as ReactMouseEvent } from 'react';
import { ReaderSearch, searchLimit, textExcerpt, type SearchHit } from './reader-search';
import { ReaderSlot, useBookmarkLocation, useReaderPanel } from './reader-shell';
import { NarrationContextMenu, narrationMenuKey, textCursorAtPoint, type NarrationMenuPosition } from './narration-context';

type TextPosition = { encoding: string; offset: number; lineFraction?: number };
type TextAnchor = { offset: number; lineFraction: number };
type LocatedSelection = TextSelection & { anchor?: { x: number; y: number; below: boolean } };
type TextNarrationPosition = NarrationMenuPosition & { offset: number; paragraphOffset: number };

function scrollPageHeight(element: HTMLElement) {
  const lineHeight = Number.parseFloat(getComputedStyle(element).lineHeight) || 1;
  return Math.max(lineHeight, Math.floor(element.clientHeight / lineHeight) * lineHeight);
}

function scrollTextPage(element: HTMLElement, direction: number) {
  element.scrollTop += direction * scrollPageHeight(element);
}

function scrollToAnchor(element: HTMLElement, { offset, lineFraction }: TextAnchor) {
  if (!offset && !lineFraction) {
    element.scrollTop = 0;
    return;
  }
  const line = element.querySelector<HTMLElement>('[data-offset="' + offset + '"]');
  if (line) element.scrollTop = line.offsetTop + line.offsetHeight * lineFraction;
}

function anchorAtScroll(element: HTMLElement): TextAnchor {
  const children = element.children as HTMLCollectionOf<HTMLElement>;
  let low = 0;
  let high = children.length - 1;
  while (low < high) {
    const middle = Math.ceil((low + high) / 2);
    if (children[middle].offsetTop <= element.scrollTop) low = middle;
    else high = middle - 1;
  }
  const line = children[low];
  return {
    offset: Number(line?.dataset.offset ?? 0),
    lineFraction: line?.offsetHeight
      ? Math.max(0, Math.min(1, (element.scrollTop - line.offsetTop) / line.offsetHeight))
      : 0
  };
}

function textLine(element: HTMLElement, node: Node | null | undefined) {
  const line = (node instanceof Element ? node : node?.parentElement)?.closest<HTMLElement>('[data-offset]');
  return line?.parentElement === element && line.textContent?.trim() ? line : undefined;
}

export function TextReader({ preview, bytes, id, initialPosition, appearance, title }: { preview?: boolean; bytes: Uint8Array; id: string; initialPosition?: string; appearance: Appearance; title: string }) {
  const tr = useTranslation();
  const startListening = useListening();
  const openPanel = useReaderPanel();
  const [saved, setSaved] = useState<TextPosition>();
  const [encoding, setEncoding] = useState('');
  const [paged, setPaged] = useState(() => localStorage.getItem('txt-reading-mode') === 'page');
  const [progress, setProgress] = useState({ page: 0, pages: 1, start: true, end: true });
  const [error, setError] = useState('');
  const [selection, setSelection] = useState<LocatedSelection>();
  const [narrationMenu, setNarrationMenu] = useState<TextNarrationPosition>();
  const [annotationSelection, setAnnotationSelection] = useState<TextSelection>();
  const [savingHighlight, setSavingHighlight] = useState(false);
  const [translationRequest, setTranslationRequest] = useState<{ text: string; revision: number }>();
  const [originalPosition, setOriginalPosition] = useState<TextAnchor>();
  const [annotations, setAnnotations] = useState<Annotation[]>([]);
  const viewport = useRef<HTMLElement>(null);
  const syncScroll = useRef<() => void>(undefined);
  const position = useRef<TextAnchor>({ offset: 0, lineFraction: 0 });
  const previous = useRef('');
  useBookmarkLocation(useCallback(() => {
    const element = viewport.current;
    if (!preview && encoding && element) return JSON.stringify({ encoding, ...anchorAtScroll(element) });
  }, [encoding, preview]));
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
            const lineFraction = Number.isFinite(position.lineFraction)
              ? Math.max(0, Math.min(1, position.lineFraction))
              : 0;
            setSaved({ encoding: position.encoding, offset: position.offset, lineFraction });
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
    const savedLine = saved?.encoding === encoding && lines.findLast(line => line.offset <= saved.offset);
    position.current = { offset: savedLine ? savedLine.offset : 0, lineFraction: savedLine ? saved?.lineFraction ?? 0 : 0 };
    scrollToAnchor(element, position.current);
    function persist() {
      const value = JSON.stringify({ encoding, ...position.current });
      if (value === previous.current) return;
      previous.current = value;
      if (!preview) void window.seaReader.savePosition(id, value).catch(() => {
        if (previous.current === value) previous.current = '';
      });
    }
    let timer: ReturnType<typeof setTimeout> | undefined;
    function updateProgress() {
      if (!element) return;
      const maximum = Math.max(0, element.scrollHeight - element.clientHeight);
      const pageHeight = scrollPageHeight(element);
      const pages = 1 + Math.ceil(maximum / pageHeight);
      const start = element.scrollTop < 1;
      const end = element.scrollTop >= maximum - 1;
      const page = end
        ? pages - 1
        : Math.min(pages - 2, Math.floor((element.scrollTop + 1) / pageHeight));
      setProgress(current => current.page === page && current.pages === pages &&
        current.start === start && current.end === end ? current : { page, pages, start, end });
    }
    let lastScrollTop = element.scrollTop;
    function onScroll() {
      if (!element || lastScrollTop === element.scrollTop) return;
      lastScrollTop = element.scrollTop;
      updateProgress();
      setSelection(undefined);
      setNarrationMenu(undefined);
      position.current = anchorAtScroll(element);
      clearTimeout(timer);
      timer = setTimeout(persist, 250);
    }
    persist();
    updateProgress();
    let viewportWidth = element.clientWidth;
    const observer = new ResizeObserver(() => {
      if (element.clientWidth !== viewportWidth) {
        viewportWidth = element.clientWidth;
        setSelection(undefined);
        setNarrationMenu(undefined);
        scrollToAnchor(element, position.current);
      }
      updateProgress();
    });
    observer.observe(element);
    syncScroll.current = onScroll;
    element.addEventListener('scroll', onScroll, { passive: true });
    return () => {
      clearTimeout(timer);
      observer.disconnect();
      element.removeEventListener('scroll', onScroll);
      syncScroll.current = undefined;
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

  useEffect(() => {
    if (!paged || !encoding) return;
    function onKeyDown(event: KeyboardEvent) {
      const element = viewport.current;
      if (!element || element.closest('[inert], .reader-workspace[data-panel]')) return;
      const direction = pageKeyDelta(event, loadPageTurning());
      if (!direction) return;
      event.preventDefault();
      scrollTextPage(element, direction);
      syncScroll.current?.();
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [paged, encoding]);

  function captureSelection() {
    if (narrationMenu) return;
    const selection = window.getSelection();
    const element = viewport.current;
    setSelection(undefined);
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
    const bounds = element.getBoundingClientRect();
    const rect = Array.from(range.getClientRects()).findLast(rect => rect.top >= bounds.top && rect.bottom <= bounds.bottom);
    let anchor: LocatedSelection['anchor'];
    if (rect) {
      const below = rect.top - bounds.top < 78;
      anchor = { x: (rect.left + rect.right) / 2, y: below ? rect.bottom : rect.top, below };
    }
    setSelection({ position: JSON.stringify({ encoding, offset, end }), quotation, anchor });
  }

  function turnPage(direction: number) {
    const element = viewport.current;
    if (!element) return;
    scrollTextPage(element, direction);
    syncScroll.current?.();
  }

  function jumpToLine(target: number) {
    const element = viewport.current;
    if (!element) return;
    if (position.current.offset !== target || position.current.lineFraction) setOriginalPosition(position.current);
    position.current = { offset: target, lineFraction: 0 };
    scrollToAnchor(element, position.current);
    syncScroll.current?.();
    openPanel();
    element.focus({ preventScroll: true });
  }

  function seekPage(page: number) {
    const element = viewport.current;
    if (!element) return;
    element.scrollTop = page * scrollPageHeight(element);
    syncScroll.current?.();
  }

  async function saveHighlight() {
    if (preview || savingHighlight || !selection) return;
    setError('');
    setSavingHighlight(true);
    try {
      const existingNote = annotations.find(item => item.position === selection.position)?.note ?? '';
      await window.seaReader.saveAnnotation(id, selection.position, selection.quotation, existingNote);
      setAnnotations(await window.seaReader.listAnnotations(id));
    } catch {
      setError(tr('无法保存划线。', 'Could not save the highlight.', 'ハイライトを保存できませんでした。'));
    } finally {
      setSavingHighlight(false);
    }
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

  function openNarrationMenu(element: HTMLElement, line: HTMLElement, point: number, x: number, y: number) {
    const paragraphOffset = Number(line.dataset.offset);
    const offset = paragraphOffset + Math.max(0, Math.min(point, (line.textContent?.length ?? 1) - 1));
    setSelection(undefined);
    setNarrationMenu({ offset, paragraphOffset, x, y, restoreFocus: () => element.focus({ preventScroll: true }) });
  }

  function contextNarration(event: ReactMouseEvent<HTMLElement>) {
    const element = event.currentTarget;
    const line = event.target instanceof Node ? textLine(element, event.target) : undefined;
    if (!line) return;
    const { clientX: x, clientY: y } = event;
    const lineRange = document.createRange();
    lineRange.selectNodeContents(line);
    const hitsText = Array.from(lineRange.getClientRects()).some(rect =>
      x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom
    );
    if (!hitsText) return;
    const caret = textCursorAtPoint(document, x, y);
    if (!caret || !line.contains(caret.startContainer)) return;
    event.preventDefault();
    openNarrationMenu(element, line, caret.startOffset, x, y);
  }

  function keyboardNarration(event: ReactKeyboardEvent<HTMLElement>) {
    if (!narrationMenuKey(event)) return;
    const element = event.currentTarget;
    const nativeSelection = window.getSelection();
    const bounds = element.getBoundingClientRect();
    let line = textLine(element, nativeSelection?.anchorNode);
    let point = nativeSelection?.anchorNode?.nodeType === Node.TEXT_NODE ? nativeSelection.anchorOffset : 0;
    const visible = (candidate: HTMLElement) => {
      const rect = candidate.getBoundingClientRect();
      return rect.bottom > bounds.top && rect.top < bounds.bottom;
    };
    if (!line || !visible(line)) {
      const { offset } = anchorAtScroll(element);
      let candidate = element.querySelector<HTMLElement>('[data-offset="' + offset + '"]');
      while (candidate && visible(candidate) && !candidate.textContent?.trim()) {
        candidate = candidate.nextElementSibling as HTMLElement | null;
      }
      line = candidate && visible(candidate) ? candidate : undefined;
      point = 0;
    }
    const text = line?.firstChild;
    if (!line || !text) return;
    const caret = document.createRange();
    caret.setStart(text, point);
    caret.collapse(true);
    const rect = caret.getBoundingClientRect();
    event.preventDefault();
    openNarrationMenu(element, line, point, rect.left, Math.max(bounds.top, Math.min(bounds.bottom, rect.bottom)));
  }

  if (!encoding) return <p>{tr('正在读取…', 'Loading…', '読み込み中…')}</p>;
  const progressLabel = tr('页 ', 'Page ', 'ページ ') + (progress.page + 1) + ' / ' + progress.pages;
  return <>
    {error && <p role="alert">{error}</p>}
    <ReaderSlot name="appearance">
      <label>{tr('文本编码', 'Text encoding', '文字コード')} <select value={encoding} onChange={event => {
        setSaved(undefined);
        setSelection(undefined);
        setNarrationMenu(undefined);
        setAnnotationSelection(undefined);
        setOriginalPosition(undefined);
        setEncoding(event.target.value);
      }}>
        <option value="utf-8">UTF-8</option><option value="gb18030">GB18030</option>
        <option value="shift_jis">Shift_JIS</option><option value="utf-16le">UTF-16 LE</option>
        <option value="utf-16be">UTF-16 BE</option>
      </select></label>
    </ReaderSlot>
    <ReaderSlot name="listen">
      <button disabled={!lines.some(line => line.text.trim())} onClick={() => listen()}>{tr('听书', 'Listen', '読み上げ')}</button>
    </ReaderSlot>
    <ReaderSlot name="flow">
      <button aria-pressed={!paged} aria-label={tr(
        paged ? '切换阅读方式，当前左右翻页' : '切换阅读方式，当前上下滚动',
        paged ? 'Change reading mode, currently paginated' : 'Change reading mode, currently continuous',
        paged ? '読書モードを切り替え、現在はページ送り' : '読書モードを切り替え、現在は縦スクロール'
      )} onClick={() => {
        setPaged(!paged);
        savePreference('txt-reading-mode', paged ? 'scroll' : 'page');
      }}>{paged ? tr('左右翻页', 'Pages', 'ページ送り') : tr('上下滚动', 'Continuous scroll', '連続スクロール')}</button>
    </ReaderSlot>
    <ReaderSlot name="paging">
      <button disabled={progress.start} onClick={() => turnPage(-1)} aria-label={tr('上一页', 'Previous page', '前のページ')}>‹</button>
      <input type="range" min={0} max={progress.pages - 1} value={progress.page} disabled={progress.pages === 1}
        aria-label={tr('阅读位置', 'Reading position', '読書位置')} aria-valuetext={progressLabel}
        onChange={event => seekPage(Number(event.currentTarget.value))} />
      <output>{progressLabel}</output>
      <button disabled={progress.end} onClick={() => turnPage(1)} aria-label={tr('下一页', 'Next page', '次のページ')}>›</button>
    </ReaderSlot>
    <ReaderSlot name="focusProgress">{progressLabel}</ReaderSlot>
    <ReaderSlot name="navigation">
      <div className="reader-controls">
        <button onClick={() => seekPage(0)} disabled={progress.start}>{tr('前往开头', 'Go to beginning', '先頭へ移動')}</button>
        <button onClick={() => seekPage(progress.pages - 1)} disabled={progress.end}>{tr('前往末尾', 'Go to end', '末尾へ移動')}</button>
      </div>
      {!preview && <Annotations bookId={id} selection={annotationSelection} items={annotations} onChange={setAnnotations} onJump={target => {
        try {
          const position = JSON.parse(target);
          if (position.encoding !== encoding) {
            new TextDecoder(position.encoding);
            setSelection(undefined);
            setOriginalPosition(undefined);
            setSaved(position);
            setEncoding(position.encoding);
            openPanel();
            viewport.current?.focus({ preventScroll: true });
          } else {
            const line = lines.findLast(line => line.offset <= position.offset);
            if (line) jumpToLine(line.offset);
          }
        } catch {
          setError('无法跳转到此标注。');
        }
      }} />}
    </ReaderSlot>
    <ReaderSlot name="tools">
      <OcrPanel translationRequest={translationRequest} />
    </ReaderSlot>
    <ReaderSlot name="search">
      <ReaderSearch onSelect={jumpToLine} search={async query => {
        const results: SearchHit<number>[] = [];
        const needle = query.toLowerCase();
        for (const line of lines) {
          const match = line.text.toLowerCase().indexOf(needle);
          if (match >= 0) results.push({ target: line.offset, excerpt: textExcerpt(line.text, match, query.length) });
          if (results.length >= searchLimit) break;
        }
        return results;
      }} />
    </ReaderSlot>
    <article className={paged ? 'txt-view txt-paged' : 'txt-view'} style={{
      fontFamily: appearance.font, fontSize: appearance.size,
      lineHeight: appearance.lineHeight, paddingInline: appearance.margin
    }} tabIndex={0} aria-label={tr('TXT 正文', 'TXT text', 'TXT 本文')} ref={viewport}
      onPointerUp={captureSelection} onKeyUp={captureSelection} onContextMenu={contextNarration} onKeyDown={keyboardNarration}>
      {lines.map(line => <div data-offset={line.offset} key={line.offset}>{line.text || '\u00a0'}</div>)}
    </article>
    {narrationMenu && <NarrationContextMenu position={narrationMenu} onClose={() => setNarrationMenu(undefined)}
      onHere={() => listen(narrationMenu.offset)} onParagraph={() => listen(narrationMenu.paragraphOffset)} />}
    <ReaderSlot name="selection">
      {(selection || originalPosition) && <div className="reader-controls" role="group" aria-label={tr('正文操作', 'Reading actions', '本文の操作')}
        style={selection?.anchor ? {
          position: 'fixed',
          left: 'clamp(min(232px, 50vw), ' + selection.anchor.x + 'px, max(calc(100vw - 232px), 50vw))',
          top: selection.anchor.y,
          transform: selection.anchor.below ? 'translate(-50%, 8px)' : 'translate(-50%, calc(-100% - 8px))',
          maxWidth: 'min(440px, calc(100vw - 24px))'
        } : undefined}>
        {selection && <>
          <button onClick={() => {
            setTranslationRequest(current => ({ text: selection.quotation, revision: (current?.revision ?? 0) + 1 }));
            openPanel('tools');
          }}>{tr('翻译选区', 'Translate selection', '選択範囲を翻訳')}</button>
          <button onClick={() => listenFromSelection(false)}>{tr('从这里听起', 'Read from here', 'ここから読み上げ')}</button>
          <button onClick={() => listenFromSelection(true)}>{tr('从本段听起', 'Read from this paragraph', 'この段落から読み上げ')}</button>
          {!preview && <>
            <button disabled={savingHighlight} onClick={() => { void saveHighlight(); }}>{tr('划线', 'Highlight', 'ハイライト')}</button>
            <button onClick={() => { setAnnotationSelection({ ...selection }); openPanel('navigation'); }}>{tr('记笔记', 'Add note', 'メモを追加')}</button>
          </>}
        </>}
        {originalPosition && <button onClick={() => {
          const element = viewport.current;
          position.current = originalPosition;
          if (element) {
            scrollToAnchor(element, originalPosition);
            syncScroll.current?.();
            element.focus({ preventScroll: true });
          }
          setOriginalPosition(undefined);
        }}>{tr('返回原位置', 'Return to previous position', '元の位置に戻る')}</button>}
      </div>}
    </ReaderSlot>
  </>;
}
