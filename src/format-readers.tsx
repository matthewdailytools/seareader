import { savePreference } from './preferences';
import { loadPageTurning, pageKeyDelta } from './page-turning';
import { useTranslation } from './locale';
import { speechParts, useListening } from './listening';
import { OcrPanel } from './ocr-panel';
import { Annotations, type Annotation, type TextSelection } from './annotations';
import type { Appearance } from './appearance';
import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import ePub, { type Rendition } from 'epubjs';
import { getDocument, GlobalWorkerOptions, TextLayer, type PDFDocumentProxy } from 'pdfjs-dist';
import 'pdfjs-dist/web/pdf_viewer.css';
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';

import { Outline, type OutlineEntry } from './outline';
import { ReaderSearch, searchLimit, textExcerpt, type SearchHit } from './reader-search';
import { ReaderSlot, useReaderPanel } from './reader-shell';
import { NarrationContextMenu, keyboardTextCursor, narrationMenuKey, textCursorAtPoint, type NarrationMenuPosition } from './narration-context';
import type Section from 'epubjs/types/section';
import type { NavItem } from 'epubjs/types/navigation';
import type { Location } from 'epubjs/types/rendition';
import type Contents from 'epubjs/types/contents';

GlobalWorkerOptions.workerSrc = workerUrl;

function epubOutline(items: NavItem[]): OutlineEntry<string>[] {
  return items.map(item => ({
    title: item.label.trim(),
    target: item.href,
    children: item.subitems && epubOutline(item.subitems)
  }));
}

type PdfOutlineItem = {
  title: string;
  dest: string | unknown[] | null;
  items: PdfOutlineItem[];
};

function pdfOutline(items: PdfOutlineItem[]): OutlineEntry<PdfOutlineItem['dest']>[] {
  return items.flatMap(item => {
    const children = pdfOutline(item.items);
    return item.dest ? [{ title: item.title, target: item.dest, children }] : children;
  });
}

type ReaderProps = { preview?: boolean; title: string; bytes: Uint8Array; id: string; initialPosition?: string };
type TranslationRequest = { text: string; revision: number };
const narrationBlocks = 'p, li, h1, h2, h3, h4, h5, h6, blockquote, pre';
const editableOrLink = 'a, button, input, textarea, select, [contenteditable]';

function epubChapterTitle(entries: OutlineEntry<string>[], href: string): string | undefined {
  for (const entry of entries) {
    if (entry.target.split('#', 1)[0] === href) return entry.title;
    const childTitle = entry.children && epubChapterTitle(entry.children, href);
    if (childTitle) return childTitle;
  }
}

async function saveHighlight(bookId: string, selection: TextSelection) {
  const annotations = await window.seaReader.listAnnotations(bookId);
  const note = annotations.find(annotation => annotation.position === selection.position)?.note ?? '';
  await window.seaReader.saveAnnotation(bookId, selection.position, selection.quotation, note);
  return window.seaReader.listAnnotations(bookId);
}

export function EpubReader({ preview, title, bytes, id, initialPosition, appearance }: ReaderProps & { appearance: Appearance }) {
  const tr = useTranslation();
  const openReaderPanel = useReaderPanel();
  const container = useRef<HTMLDivElement>(null);
  const rendition = useRef<Rendition | null>(null);
  const bookRef = useRef<ReturnType<typeof ePub> | null>(null);
  const lastCfi = useRef(initialPosition);
  const [continuous, setContinuous] = useState(() => {
    return localStorage.getItem('epub-reading-mode') === 'scroll';
  });
  const [error, setError] = useState('');
  const startListening = useListening();
  const [preparing, setPreparing] = useState(false);
  const listeningRequest = useRef<AbortController | null>(null);
  useEffect(() => () => listeningRequest.current?.abort(), [id]);
  const [ready, setReady] = useState(false);
  const [selection, setSelection] = useState<TextSelection>();
  const [annotationSelection, setAnnotationSelection] = useState<TextSelection>();
  const [translationRequest, setTranslationRequest] = useState<TranslationRequest>();
  const [narrationMenu, setNarrationMenu] = useState<NarrationMenuPosition & { cfi: string }>();
  const [returnPosition, setReturnPosition] = useState<string>();
  const [annotations, setAnnotations] = useState<Annotation[]>([]);
  const [contents, setContents] = useState<OutlineEntry<string>[]>([]);
  const [spine, setSpine] = useState<{ index: number; href: string }[]>([]);
  const [location, setLocation] = useState<Location>();

  useEffect(() => {
    const key = 'epub-position:' + id;
    const book = ePub(Uint8Array.from(bytes).buffer);
    let active = true;
    function reportOpenFailure() {
      if (active) setError('无法打开 EPUB。文件可能损坏或受保护。');
    }
    book.on('openFailed', reportOpenFailure);
    bookRef.current = book;
    const view = book.renderTo(container.current!, {
      width: '100%', height: '100%', allowScriptedContent: false,
      manager: continuous ? 'continuous' : 'default',
      flow: continuous ? 'scrolled-doc' : 'paginated', defaultDirection: loadPageTurning().direction
    });
    view.on('displayError', reportOpenFailure);
    rendition.current = view;
    view.hooks.content.register((contents: Contents) => {
      // A short last chapter needs enough room below it to reach the top of the scrolling viewport.
      if (continuous && contents.sectionIndex === book.spine.last()?.index) {
        const padding = (container.current?.clientHeight ?? 0) - contents.textHeight();
        if (padding > 0) {
          const end = contents.document.createElement('div');
          end.setAttribute('aria-hidden', 'true');
          end.style.height = padding + 'px';
          contents.content.append(end);
          contents.emit('expand');
        }
      }
      function showNarrationAt(caret: Range, x: number, y: number) {
        const frame = contents.window.frameElement as HTMLElement | null;
        if (!frame || !active) return false;
        try {
          const box = frame.getBoundingClientRect();
          setNarrationMenu({
            cfi: contents.cfiFromRange(caret), x: box.left + x, y: box.top + y,
            restoreFocus: () => (frame.isConnected ? frame : container.current)?.focus({ preventScroll: true })
          });
          return true;
        } catch {
          return false;
        }
      }

      contents.document.addEventListener('pointerdown', () => { if (active) setNarrationMenu(undefined); });
      contents.document.addEventListener('contextmenu', event => {
        if (event.defaultPrevented) return;
        const target = event.target as Element | null;
        const block = target?.closest?.(narrationBlocks);
        if (!block || target?.closest(editableOrLink)) return;
        const caret = textCursorAtPoint(contents.document, event.clientX, event.clientY);
        if (caret && block.contains(caret.startContainer) && showNarrationAt(caret, event.clientX, event.clientY)) {
          event.preventDefault();
        }
      });
      contents.document.addEventListener('keydown', event => {
        if (event.defaultPrevented) return;
        if (narrationMenuKey(event)) {
          const target = event.target as Element | null;
          if (target?.closest?.(editableOrLink)) return;
          const caret = keyboardTextCursor(target?.closest?.(narrationBlocks) ?? contents.content);
          if (caret) {
            const box = caret.getBoundingClientRect();
            if (showNarrationAt(caret, box.left, box.bottom)) event.preventDefault();
          }
          return;
        }
        if ((event.ctrlKey && event.key.toLowerCase() === 'f') || event.key === 'Escape') {
          const forwarded = new KeyboardEvent('keydown', { key: event.key, ctrlKey: event.ctrlKey, cancelable: true });
          if (!window.dispatchEvent(forwarded)) event.preventDefault();
          return;
        }
        const delta = continuous ? 0 : pageKeyDelta(event, loadPageTurning());
        if (delta) {
          event.preventDefault();
          void (delta < 0 ? view.prev() : view.next()).catch(() => {});
        }
      });
      contents.document.addEventListener('pointerup', () => {
        if (active && contents.document.getSelection()?.isCollapsed) setSelection(undefined);
      });
    });
    view.on('selected', (position: string, contents: { window: Window }) => {
      const quotation = contents.window.getSelection()?.toString().trim().slice(0, 12000);
      if (active && quotation) setSelection({ position, quotation });
    });
    if (!preview) void window.seaReader.listAnnotations(id).then(items => {
      if (active) setAnnotations(items);
    }).catch(() => {
      if (active) setError('无法读取标注。');
    });
    view.on('relocated', (location: Location) => {
      if (!active || !location.start?.cfi) return;
      if (lastCfi.current !== location.start.cfi) setSelection(undefined);
      lastCfi.current = location.start.cfi;
      setLocation(location);
      if (!preview) void window.seaReader.savePosition(id, location.start.cfi).catch(() => setError('无法保存阅读位置。'));
    });
    async function open() {
      try {
        const saved = lastCfi.current ?? (preview ? null : await window.seaReader.getPosition(id) ?? localStorage.getItem(key));
        if (!active) return;
        if (saved) {
          try { await view.display(saved); }
          catch { if (active) await view.display(); else return; }
        } else {
          await view.display();
        }
        if (active) {
          const sections: { index: number; href: string }[] = [];
          book.spine.each((section: Section) => {
            if (section.linear) sections.push({ index: section.index, href: section.href });
          });
          setSpine(sections);
          setReady(true);
        }
      } catch {
        reportOpenFailure();
      }
    }
    void open();
    void book.loaded.navigation.then(navigation => {
      if (active) setContents(epubOutline(navigation.toc));
    }).catch(() => {});
    return () => {
      active = false;
      book.off('openFailed', reportOpenFailure);
      view.off('displayError', reportOpenFailure);
      rendition.current = null;
      bookRef.current = null;
      book.destroy();
    };
  }, [bytes, id, continuous, preview]);

  useEffect(() => {
    const element = container.current;
    const view = rendition.current;
    if (!element || !view || !ready) return;
    const observer = new ResizeObserver(([entry]) => {
      const width = Math.floor(entry.contentRect.width);
      const height = Math.floor(entry.contentRect.height);
      if (width > 0 && height > 0 && view.location?.start) view.resize(width, height);
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [ready]);

  useEffect(() => {
    const view = rendition.current;
    if (!view || !ready) return;
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    function apply() {
      if (!view) return;
      const dark = appearance.theme === 'dark' || (appearance.theme === 'system' && media.matches);
      const sepia = appearance.theme === 'sepia';
      view.themes.override('color', dark ? '#e3ebeb' : sepia ? '#392f22' : '#1d3038', true);
      view.themes.override('background-color', dark ? '#1d292e' : sepia ? '#f4ead5' : '#ffffff', true);
      view.themes.override('line-height', String(appearance.lineHeight), true);
      view.themes.override('padding-inline', appearance.margin + 'px', true);
      view.themes.fontSize(appearance.size + 'px');
      view.themes.font(appearance.font);
    }
    apply();
    media.addEventListener('change', apply);
    return () => media.removeEventListener('change', apply);
  }, [appearance, ready]);

  useEffect(() => {
    const view = rendition.current;
    if (!view || !ready) return;
    for (const item of annotations) {
      view.annotations.highlight(item.position, {}, undefined, 'sea-highlight', { fill: '#edb830', 'fill-opacity': '.35' });
    }
    return () => {
      if (rendition.current !== view) return;
      for (const item of annotations) view.annotations.remove(item.position, 'highlight');
    };
  }, [annotations, ready]);

  async function listen(position?: string, fromParagraph = false) {
    const book = bookRef.current;
    if (!book || preparing) return;
    const request = new AbortController();
    listeningRequest.current = request;
    setPreparing(true);
    setError('');
    try {
      const selectedRange = position ? await book.getRange(position) : undefined;
      const sections: Section[] = [];
      book.spine.each((section: Section) => sections.push(section));
      const parts: string[] = [];
      const chapters: number[] = [];
      let startIndex: number | undefined;
      for (const section of sections) {
        if (request.signal.aborted) return;
        const root = await section.load(book.load.bind(book));
        if (request.signal.aborted) return;
        const body = root.querySelector('body') ?? root;
        const candidates = Array.from(body.querySelectorAll(narrationBlocks));
        const blocks = candidates.filter(block => !candidates.some(other => other !== block && block.contains(other)));
        const chapterStart = parts.length;
        for (const block of blocks.length ? blocks : [body]) {
          const text = block.textContent ?? '';
          let from: number | undefined;
          if (selectedRange && block.contains(selectedRange.startContainer)) {
            const prefix = selectedRange.cloneRange();
            prefix.selectNodeContents(block);
            prefix.setEnd(selectedRange.startContainer, selectedRange.startOffset);
            from = fromParagraph ? 0 : prefix.toString().length;
          }
          let cursor = 0;
          for (const part of speechParts(text)) {
            const start = text.indexOf(part, cursor);
            cursor = start + part.length;
            if (from !== undefined && startIndex === undefined && from < cursor) {
              startIndex = parts.length;
              parts.push(part.slice(Math.max(0, from - start)));
            } else {
              parts.push(part);
            }
          }
        }
        if (parts.length > chapterStart) chapters.push(chapterStart);
      }
      if (position && startIndex === undefined) {
        setError('无法从该选区起听，请选择正文段落中的文字。');
      } else if (parts.length) {
        startListening({ id: preview ? '' : 'epub:' + id, title, parts, chapters }, startIndex);
      } else {
        setError('此 EPUB 没有可朗读的正文。');
      }
    } catch {
      if (!request.signal.aborted) setError('无法准备 EPUB 听书。');
    } finally {
      if (!request.signal.aborted) setPreparing(false);
    }
  }

  async function search(query: string, signal: AbortSignal) {
    const book = bookRef.current;
    const results: SearchHit<string>[] = [];
    if (!book) return results;
    const sections: Section[] = [];
    book.spine.each((section: Section) => sections.push(section));
    for (const section of sections) {
      if (signal.aborted || results.length >= searchLimit) break;
      await section.load(book.load.bind(book));
      if (signal.aborted) break;
      const matches = section.find(query) as unknown as { cfi: string; excerpt: string }[];
      results.push(...matches.slice(0, searchLimit - results.length).map(match => ({
        target: match.cfi, excerpt: match.excerpt
      })));
    }
    return results;
  }

  const chapterIndex = location ? spine.findIndex(section => section.index === location.start.index) : -1;
  const positionLabel = chapterIndex >= 0
    ? tr('章', 'Chapter', '章') + ' ' + (chapterIndex + 1) + ' / ' + spine.length
    : tr('正在读取…', 'Loading…', '読み込み中…');
  const chapterTitle = location && epubChapterTitle(contents, location.start.href);

  function step(delta: number) {
    const view = rendition.current;
    if (!view) return;
    setSelection(undefined);
    void (delta < 0 ? view.prev() : view.next()).catch(() => setError('无法翻页。'));
  }

  function jumpTo(target: string, remember = true) {
    const view = rendition.current;
    if (!view) return;
    const origin = lastCfi.current;
    void view.display(target).then(() => {
      setSelection(undefined);
      setReturnPosition(remember ? origin : undefined);
      openReaderPanel();
    }).catch(() => setError('无法跳转到此位置。'));
  }

  return <div className="epub-reader" onKeyDown={event => {
    if (!ready || continuous) return;
    const delta = pageKeyDelta(event.nativeEvent, loadPageTurning());
    if (delta) {
      event.preventDefault();
      step(delta);
    }
  }}>
    {error && <p role="alert">{error}</p>}
    <ReaderSlot name="paging">
      <button disabled={!ready || Boolean(location?.atStart)} onClick={() => step(-1)}
        aria-label={tr('上一页', 'Previous page', '前のページ')}>‹</button>
      <input type="range" min={0} max={Math.max(0, spine.length - 1)} value={Math.max(0, chapterIndex)}
        disabled={!ready || spine.length <= 1} aria-label={tr('阅读位置', 'Reading position', '読書位置')}
        aria-valuetext={positionLabel} onChange={event => {
          const chapter = spine[Number(event.target.value)];
          if (chapter) jumpTo(chapter.href);
        }} />
      <output title={chapterTitle}>{positionLabel}</output>
      <button disabled={!ready || Boolean(location?.atEnd)} onClick={() => step(1)}
        aria-label={tr('下一页', 'Next page', '次のページ')}>›</button>
    </ReaderSlot>
    <ReaderSlot name="focusProgress">{positionLabel}</ReaderSlot>
    <ReaderSlot name="flow">
      <button disabled={!ready} aria-pressed={continuous} onClick={() => {
        savePreference('epub-reading-mode', continuous ? 'page' : 'scroll');
        setReady(false);
        setSelection(undefined);
        setError('');
        setContinuous(!continuous);
      }}>{continuous ? tr('上下滚动', 'Continuous scroll', '連続スクロール') : tr('左右翻页', 'Pages', 'ページ送り')}</button>
    </ReaderSlot>
    <ReaderSlot name="navigation">
      <Outline entries={contents} onSelect={jumpTo} />
      {ready && !preview && <Annotations bookId={id} items={annotations} selection={annotationSelection} onChange={setAnnotations} onJump={jumpTo} />}
    </ReaderSlot>
    <ReaderSlot name="listen">
      <button disabled={!ready || preparing} onClick={() => void listen()}>{preparing ? tr('正在准备听书…', 'Preparing narration…', '読み上げを準備中…') : tr('听书', 'Listen', '読み上げ')}</button>
    </ReaderSlot>
    <ReaderSlot name="selection">
      {(ready && selection || returnPosition) && <div className="reader-controls">
        {ready && selection && <>
          <button onClick={() => {
            setTranslationRequest(current => ({ text: selection.quotation, revision: (current?.revision ?? 0) + 1 }));
            openReaderPanel('tools');
          }}>{tr('翻译选中文字', 'Translate selection', '選択した文字を翻訳')}</button>
          <button disabled={preparing} onClick={() => void listen(selection.position)}>{tr('从这里听起', 'Read from here', 'ここから読み上げ')}</button>
          <button disabled={preparing} onClick={() => void listen(selection.position, true)}>{tr('从本段听起', 'Read from this paragraph', 'この段落から読み上げ')}</button>
          {!preview && <>
            <button onClick={() => void saveHighlight(id, selection).then(setAnnotations, () => setError('无法保存标注。'))}>{tr('划线', 'Highlight', 'ハイライト')}</button>
            <button onClick={() => { setAnnotationSelection(selection); openReaderPanel('navigation'); }}>{tr('笔记', 'Note', 'メモ')}</button>
          </>}
        </>}
        {returnPosition && <button onClick={() => jumpTo(returnPosition, false)}>{tr('返回原位置', 'Return to previous position', '元の位置へ戻る')}</button>}
      </div>}
    </ReaderSlot>
    <ReaderSlot name="tools">{ready && <OcrPanel translationRequest={translationRequest} />}</ReaderSlot>
    <ReaderSlot name="search">{ready && <ReaderSearch search={search} onSelect={jumpTo} />}</ReaderSlot>
    {narrationMenu && <NarrationContextMenu position={narrationMenu} onClose={() => setNarrationMenu(undefined)}
      onHere={() => void listen(narrationMenu.cfi)} onParagraph={() => void listen(narrationMenu.cfi, true)} />}
    <div ref={container} className="epub-view" tabIndex={0} aria-label={tr('EPUB 正文', 'EPUB text', 'EPUB 本文')} />
  </div>;
}

type PdfListeningPosition = { page: number; prefixCharacters: number };
type PdfSelection = TextSelection & PdfListeningPosition;

function pdfPrefixCharacters(element: HTMLElement, range: Range) {
  const prefix = range.cloneRange();
  prefix.selectNodeContents(element);
  prefix.setEnd(range.startContainer, range.startOffset);
  return prefix.toString().replace(/\s/gu, '').length;
}

function offsetForPdfSelection(text: string, prefixCharacters: number) {
  let remaining = prefixCharacters;
  for (let offset = 0; offset < text.length; offset++) {
    if (/\s/u.test(text[offset])) continue;
    if (remaining === 0) return offset;
    remaining--;
  }
  return text.length;
}

type PdfMark = { page: number; rects: number[][] };

function readPdfMark(position: string): PdfMark | undefined {
  try {
    const mark = JSON.parse(position);
    if (Number.isSafeInteger(mark.page) && mark.page > 0 && Array.isArray(mark.rects) &&
        mark.rects.every((rect: unknown) => Array.isArray(rect) && rect.length === 4 &&
          rect.every(value => typeof value === 'number' && value >= 0 && value <= 1))) return mark;
  } catch { /* Ignore malformed marks. */ }
}

function PdfCanvas({ document, page, zoom, size, onError, onSelection, onNarrationMenu, annotations }: {
  document: PDFDocumentProxy;
  page: number;
  zoom: string;
  size: { width: number; height: number };
  onError: (message: string) => void;
  onSelection: (selection: PdfSelection | undefined) => void;
  onNarrationMenu: (menu: NarrationMenuPosition & PdfListeningPosition) => void;
  annotations: Annotation[];
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const text = useRef<HTMLDivElement>(null);
  useEffect(() => {
    let active = true;
    let render: ReturnType<Awaited<ReturnType<PDFDocumentProxy['getPage']>>['render']> | undefined;
    let layer: TextLayer | undefined;
    document.getPage(page).then(pdfPage => {
      if (!active || !canvas.current || !text.current) return;
      const original = pdfPage.getViewport({ scale: 1 });
      const widthScale = size.width ? size.width / original.width : 1;
      const heightScale = size.height ? size.height / original.height : 1;
      const scale = zoom === 'width' ? widthScale : zoom === 'page' ? Math.min(widthScale, heightScale) : Number(zoom);
      const density = Math.min(window.devicePixelRatio, 8192 / (Math.max(original.width, original.height) * scale));
      const viewport = pdfPage.getViewport({ scale: scale * density });
      canvas.current.width = Math.ceil(viewport.width);
      canvas.current.height = Math.ceil(viewport.height);
      canvas.current.style.width = original.width * scale + 'px';
      canvas.current.style.height = original.height * scale + 'px';
      render = pdfPage.render({ canvas: canvas.current, viewport });
      text.current.replaceChildren();
      text.current.style.setProperty('--total-scale-factor', String(scale * pdfPage.userUnit));
      layer = new TextLayer({
        textContentSource: pdfPage.streamTextContent(),
        container: text.current,
        viewport: pdfPage.getViewport({ scale })
      });
      return Promise.all([render.promise, layer.render()]);
    }).catch(() => {
      if (active) onError('无法渲染此 PDF 页面。');
    });
    return () => { active = false; render?.cancel(); layer?.cancel(); };
  }, [document, page, zoom, size, onError]);

  function capture() {
    const element = text.current;
    const selection = window.getSelection();
    if (!element || !selection?.rangeCount || selection.isCollapsed) {
      onSelection(undefined);
      return;
    }
    const range = selection.getRangeAt(0);
    if (!element.contains(range.startContainer) || !element.contains(range.endContainer)) return;
    const box = element.getBoundingClientRect();
    if (!box.width || !box.height) return;
    const normalize = (value: number) => Math.round(Math.max(0, Math.min(1, value)) * 10000) / 10000;
    const rects = Array.from(range.getClientRects()).filter(rect => rect.width > 0 && rect.height > 0).map(rect => [
      normalize((rect.left - box.left) / box.width), normalize((rect.top - box.top) / box.height),
      normalize(rect.width / box.width), normalize(rect.height / box.height)
    ]);
    const quotation = selection.toString().trim().slice(0, 12000);
    const position = JSON.stringify({ page, rects });
    if (position.length > 4096) {
      onError('选区过长，请分段标注。');
    } else if (quotation && rects.length) {
      const prefixCharacters = pdfPrefixCharacters(element, range);
      onSelection({ position, quotation, page, prefixCharacters });
    }
  }

  return <div className="pdf-sheet">
    <canvas ref={canvas} aria-label={'PDF 第 ' + page + ' 页'} />
    <div ref={text} className="textLayer" tabIndex={0} aria-label={'第 ' + page + ' 页正文'} onPointerUp={capture} onKeyUp={capture}
      onContextMenu={event => {
        const target = (event.target as Element).closest('.textLayer span');
        const layer = event.currentTarget;
        if (!target || !layer.contains(target)) return;
        const caret = textCursorAtPoint(layer.ownerDocument, event.clientX, event.clientY);
        if (!caret || !target.contains(caret.startContainer)) return;
        event.preventDefault();
        onNarrationMenu({
          x: event.clientX, y: event.clientY, page, prefixCharacters: pdfPrefixCharacters(layer, caret),
          restoreFocus: () => layer.focus({ preventScroll: true })
        });
      }} onKeyDown={event => {
        if (!narrationMenuKey(event)) return;
        const layer = event.currentTarget;
        const caret = keyboardTextCursor(layer);
        if (!caret) return;
        event.preventDefault();
        const box = caret.getBoundingClientRect();
        onNarrationMenu({
          x: box.left, y: box.bottom, page, prefixCharacters: pdfPrefixCharacters(layer, caret),
          restoreFocus: () => layer.focus({ preventScroll: true })
        });
      }} />
    <div className="pdf-marks" aria-hidden="true">
      {annotations.flatMap(item => {
        const mark = readPdfMark(item.position);
        return mark?.page === page ? mark.rects.map(([left, top, width, height], index) =>
          <span key={item.id + ':' + index} style={{ left: left * 100 + '%', top: top * 100 + '%', width: width * 100 + '%', height: height * 100 + '%' }} />
        ) : [];
      })}
    </div>
  </div>;
}
export function PdfReader({ preview, title, bytes, id, initialPosition }: ReaderProps) {
  const tr = useTranslation();
  const openReaderPanel = useReaderPanel();
  const surface = useRef<HTMLDivElement>(null);
  const key = 'pdf-position:' + id;
  const [recognized, setRecognized] = useState<Record<number, string>>({});
  useEffect(() => {
    let active = true;
    if (!preview) void window.seaReader.listRecognizedPages(id).then(pages => {
      if (active) setRecognized(Object.fromEntries(pages.map(item => [item.page, item.text])));
    }).catch(() => {
      if (active) setError('无法读取已保存的识别文字。');
    });
    return () => { active = false; };
  }, [id]);
  const [selection, setSelection] = useState<PdfSelection>();
  const [annotationSelection, setAnnotationSelection] = useState<TextSelection>();
  const [translationRequest, setTranslationRequest] = useState<TranslationRequest>();
  const [narrationMenu, setNarrationMenu] = useState<NarrationMenuPosition & PdfListeningPosition>();
  const [returnPage, setReturnPage] = useState<number>();
  const [annotations, setAnnotations] = useState<Annotation[]>([]);
  useEffect(() => {
    let active = true;
    if (!preview) void window.seaReader.listAnnotations(id).then(items => {
      if (active) setAnnotations(items);
    }).catch(() => {
      if (active) setError('无法读取标注。');
    });
    return () => { active = false; };
  }, [id]);
  const [document, setDocument] = useState<PDFDocumentProxy>();
  const [page, setPage] = useState(1);
  const [contents, setContents] = useState<OutlineEntry<PdfOutlineItem['dest']>[]>([]);
  const [zoom, setZoom] = useState('width');
  const [continuous, setContinuous] = useState(() => localStorage.getItem('pdf-reading-mode') === 'scroll');
  const [size, setSize] = useState({ width: 0, height: 0 });
  const stride = Math.max(280, size.height + 16);
  const trackedPage = useRef(page);
  trackedPage.current = page;
  useLayoutEffect(() => {
    if (continuous && surface.current) surface.current.scrollTop = (trackedPage.current - 1) * stride;
  }, [continuous, stride, document]);
  function navigate(target: number, scroll = continuous) {
    if (!document) return;
    const next = Math.max(1, Math.min(target, document.numPages));
    if (surface.current) surface.current.scrollTop = scroll ? (next - 1) * stride : 0;
    setSelection(undefined);
    if (next !== page) setTranslationRequest(undefined);
    setPage(next);
  }
  function jumpTo(target: number) {
    if (target !== page) setReturnPage(page);
    navigate(target);
    openReaderPanel();
  }
  useEffect(() => {
    const element = surface.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => {
      setSize({ width: entry.contentRect.width, height: entry.contentRect.height });
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    if (!document) return;
    function turn(event: KeyboardEvent) {
      const delta = pageKeyDelta(event, loadPageTurning());
      if (!delta || surface.current?.closest('[inert]')) return;
      event.preventDefault();
      navigate(page + delta);
    }
    window.addEventListener('keydown', turn);
    return () => window.removeEventListener('keydown', turn);
  }, [document, continuous, page, stride]);
  const [error, setError] = useState('');
  const startListening = useListening();
  const [preparing, setPreparing] = useState(false);
  const listeningRequest = useRef<AbortController | null>(null);
  useEffect(() => () => listeningRequest.current?.abort(), [id]);

  useEffect(() => {
    const task = getDocument({ data: Uint8Array.from(bytes) });
    let active = true;
    Promise.all([task.promise, initialPosition ?? (preview ? null : window.seaReader.getPosition(id))]).then(([pdf, saved]) => {
      if (!active) return;
      setPage(Math.max(1, Math.min(Number(saved ?? localStorage.getItem(key)) || 1, pdf.numPages)));
      setDocument(pdf);
      void pdf.getOutline().then(items => {
        if (active) setContents(pdfOutline(items ?? []));
      }).catch(() => {});
    }, () => {
      if (active) setError('无法打开 PDF。文件可能损坏或受保护。');
    });
    return () => { active = false; void task.destroy(); };
  }, [bytes, id, initialPosition, key]);

  useEffect(() => {
    if (!document) return;
    if (!preview) void window.seaReader.savePosition(id, String(page)).catch(() => setError('无法保存阅读位置。'));
  }, [document, page, id]);
  async function listen(from?: PdfListeningPosition, fromParagraph = false) {
    if (!document || preparing) return;
    const request = new AbortController();
    listeningRequest.current = request;
    setPreparing(true);
    setError('');
    try {
      const parts: string[] = [];
      const chapters: number[] = [];
      let skipped = 0;
      let startIndex: number | undefined;
      for (let pageNumber = 1; pageNumber <= document.numPages; pageNumber++) {
        if (request.signal.aborted) return;
        let text = pageNumber === from?.page ? undefined : recognized[pageNumber]?.trim();
        if (!text) {
          const pdfPage = await document.getPage(pageNumber);
          const content = await pdfPage.getTextContent();
          text = content.items.map(item => 'str' in item ? item.str + (item.hasEOL ? '\n' : ' ') : '').join('');
        }
        const chapter = speechParts(text);
        if (!chapter.length) {
          skipped++;
          continue;
        }
        chapters.push(parts.length);
        if (from?.page === pageNumber) {
          const anchor = offsetForPdfSelection(text, from.prefixCharacters);
          const start = fromParagraph ? text.lastIndexOf('\n', Math.max(0, anchor - 1)) + 1 : anchor;
          let cursor = 0;
          for (const part of chapter) {
            const at = text.indexOf(part, cursor);
            cursor = at + part.length;
            if (startIndex === undefined && start < cursor) {
              startIndex = parts.length;
              parts.push(part.slice(Math.max(0, start - at)));
            } else {
              parts.push(part);
            }
          }
        } else {
          for (const part of chapter) parts.push(part);
        }
      }
      if (request.signal.aborted) return;
      if (from && startIndex === undefined) {
        setError('无法从该选区起听，请重新选择正文。');
        return;
      }
      if (parts.length) startListening({ id: preview ? '' : 'pdf:' + id, title, parts, chapters }, startIndex);
      if (skipped) setError(parts.length ? '已跳过 ' + skipped + ' 个没有文字的页面，可先识别并保存。' : '请先识别并保存扫描页文字。');
    } catch {
      if (!request.signal.aborted) setError('无法准备 PDF 听书。');
    } finally {
      if (!request.signal.aborted) setPreparing(false);
    }
  }

  const positionLabel = document
    ? tr('页', 'Page', 'ページ') + ' ' + page + ' / ' + document.numPages
    : tr('正在读取…', 'Loading…', '読み込み中…');
  const activeSelection = selection?.page === page ? selection : undefined;

  return <>
    {error && <p role="alert">{error}</p>}
    <ReaderSlot name="listen">
      <button disabled={!document || preparing} onClick={() => void listen()}>{preparing ? tr('正在准备听书…', 'Preparing narration…', '読み上げを準備中…') : tr('听书', 'Listen', '読み上げ')}</button>
    </ReaderSlot>
    <ReaderSlot name="selection">
      {(activeSelection || returnPage) && <div className="reader-controls">
        {activeSelection && <>
          <button onClick={() => {
            setTranslationRequest(current => ({ text: activeSelection.quotation, revision: (current?.revision ?? 0) + 1 }));
            openReaderPanel('tools');
          }}>{tr('翻译选中文字', 'Translate selection', '選択した文字を翻訳')}</button>
          <button disabled={preparing} onClick={() => void listen(activeSelection)}>{tr('从这里听起', 'Read from here', 'ここから読み上げ')}</button>
          <button disabled={preparing} onClick={() => void listen(activeSelection, true)}>{tr('从本行听起', 'Read from this line', 'この行から読み上げ')}</button>
          {!preview && <>
            <button onClick={() => void saveHighlight(id, activeSelection).then(setAnnotations, () => setError('无法保存标注。'))}>{tr('划线', 'Highlight', 'ハイライト')}</button>
            <button onClick={() => { setAnnotationSelection(activeSelection); openReaderPanel('navigation'); }}>{tr('笔记', 'Note', 'メモ')}</button>
          </>}
        </>}
        {returnPage && <button onClick={() => { navigate(returnPage); setReturnPage(undefined); }}>{tr('返回原位置', 'Return to previous position', '元の位置へ戻る')}</button>}
      </div>}
    </ReaderSlot>
    <ReaderSlot name="paging">
      <button disabled={!document || page <= 1} onClick={() => navigate(page - 1)}
        aria-label={tr('上一页', 'Previous page', '前のページ')}>‹</button>
      <input type="range" min={1} max={document?.numPages ?? 1} value={page}
        disabled={!document || document.numPages <= 1} aria-label={tr('阅读位置', 'Reading position', '読書位置')}
        aria-valuetext={positionLabel} onChange={event => {
          setReturnPage(current => current ?? page);
          navigate(Number(event.target.value));
        }} />
      <output>{positionLabel}</output>
      <button disabled={!document || page >= document.numPages} onClick={() => navigate(page + 1)}
        aria-label={tr('下一页', 'Next page', '次のページ')}>›</button>
    </ReaderSlot>
    <ReaderSlot name="focusProgress">{positionLabel}</ReaderSlot>
    <ReaderSlot name="flow">
      <button disabled={!document} aria-pressed={continuous} onClick={() => {
        setContinuous(!continuous);
        setSelection(undefined);
        savePreference('pdf-reading-mode', continuous ? 'page' : 'scroll');
        if (continuous && surface.current) surface.current.scrollTop = 0;
      }}>{continuous ? tr('上下滚动', 'Continuous scroll', '連続スクロール') : tr('左右翻页', 'Pages', 'ページ送り')}</button>
    </ReaderSlot>
    <ReaderSlot name="appearance">
      <label>{tr('缩放', 'Zoom', '拡大率')} <select value={continuous ? 'page' : zoom} disabled={continuous} onChange={event => setZoom(event.target.value)}>
        <option value="width">{tr('适宽', 'Fit width', '幅に合わせる')}</option>
        <option value="page">{tr('适页', 'Fit page', 'ページに合わせる')}</option>
        {[.75, 1, 1.25, 1.5, 2].map(value =>
          <option key={value} value={value}>{value * 100}%</option>
        )}
      </select></label>
    </ReaderSlot>
    <ReaderSlot name="navigation">
      {document && <form className="reader-controls" onSubmit={event => {
        event.preventDefault();
        const target = Number(new FormData(event.currentTarget).get('page'));
        if (Number.isSafeInteger(target) && target >= 1 && target <= document.numPages) jumpTo(target);
      }}>
        <label>{tr('页码', 'Page number', 'ページ番号')} <input type="number" name="page" required min={1} max={document.numPages} key={page} defaultValue={page} /></label>
        <button>{tr('跳转', 'Go', '移動')}</button>
      </form>}
      <Outline entries={contents} onSelect={async target => {
        if (!document || !target) return;
        try {
          const destination = typeof target === 'string' ? await document.getDestination(target) : target;
          const ref = destination?.[0];
          if (typeof ref === 'number') jumpTo(ref + 1);
          else if (ref && typeof ref === 'object' && 'num' in ref && 'gen' in ref &&
                   typeof ref.num === 'number' && typeof ref.gen === 'number') {
            jumpTo(await document.getPageIndex({ num: ref.num, gen: ref.gen }) + 1);
          } else {
            setError('无法打开此目录位置。');
          }
        } catch {
          setError('无法打开此目录位置。');
        }
      }} />
      {document && !preview && <Annotations bookId={id} items={annotations} selection={annotationSelection} onChange={setAnnotations} onJump={position => {
        const mark = readPdfMark(position);
        if (mark) jumpTo(mark.page);
      }} />}
    </ReaderSlot>
    <ReaderSlot name="tools">{document && <OcrPanel key={page} translationRequest={translationRequest} saved={recognized[page]} capture={async () => {
      const pdfPage = await document.getPage(page);
      const original = pdfPage.getViewport({ scale: 1 });
      const viewport = pdfPage.getViewport({ scale: Math.min(2.5, 3000 / Math.max(original.width, original.height)) });
      const canvas = window.document.createElement('canvas');
      canvas.width = Math.ceil(viewport.width);
      canvas.height = Math.ceil(viewport.height);
      await pdfPage.render({ canvas, viewport }).promise;
      return new Promise<Blob>((resolve, reject) => {
        canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error('无法生成页面预览')), 'image/png');
      });
    }} onSave={preview ? undefined : async text => {
      await window.seaReader.saveRecognizedPage(id, page, text);
      setRecognized(current => ({ ...current, [page]: text }));
    }} />}</ReaderSlot>
    <ReaderSlot name="search">{document && <ReaderSearch onSelect={jumpTo} search={async (query, signal) => {
      const results: SearchHit<number>[] = [];
      for (let number = 1; number <= document.numPages && results.length < searchLimit; number++) {
        if (signal.aborted) break;
        const pdfPage = await document.getPage(number);
        const content = await pdfPage.getTextContent();
        const nativeText = content.items.map(item => 'str' in item ? item.str : '').join(' ');
        const text = recognized[number]?.trim() || nativeText;
        const match = text.toLowerCase().indexOf(query.toLowerCase());
        if (match >= 0) results.push({
          target: number, excerpt: '第 ' + number + ' 页 · ' + textExcerpt(text, match, query.length)
        });
      }
      return results;
    }} />}</ReaderSlot>
    {narrationMenu && <NarrationContextMenu position={narrationMenu} onClose={() => setNarrationMenu(undefined)}
      onHere={() => void listen(narrationMenu)} onParagraph={() => void listen(narrationMenu, true)}
      paragraphLabel={tr('从本行听起', 'Read from this line', 'この行から読み上げ')} />}
    <div className="pdf-view" ref={surface} tabIndex={0} style={continuous ? { overflowAnchor: 'none' } : undefined}
      aria-label={tr('PDF 阅读区', 'PDF reading area', 'PDF 読書領域')} onScroll={event => {
      if (!continuous || !document) return;
      const target = Math.min(document.numPages,
        Math.floor((event.currentTarget.scrollTop + Math.min(120, stride / 2)) / stride) + 1);
      if (target !== page) setTranslationRequest(undefined);
      setPage(target);
    }}>
      {document && (continuous ? <div className="pdf-scroll-track" style={{ height: document.numPages * stride }}>
        {Array.from({ length: Math.min(document.numPages, page + 2) - Math.max(1, page - 2) + 1 }, (_, index) => {
          const number = Math.max(1, page - 2) + index;
          return <div className="pdf-scroll-slot" key={number} style={{ top: (number - 1) * stride, height: stride }}>
            <PdfCanvas document={document} page={number} zoom="page" size={size} onError={setError} onSelection={setSelection}
              onNarrationMenu={setNarrationMenu} annotations={annotations} />
          </div>;
        })}
      </div> : <PdfCanvas document={document} page={page} zoom={zoom} size={size} onError={setError} onSelection={setSelection}
        onNarrationMenu={setNarrationMenu} annotations={annotations} />)}
    </div>
  </>;
}
