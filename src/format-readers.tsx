import { savePreference } from './preferences';
import { loadPageTurning, pageKeyDelta } from './page-turning';
import { useTranslation } from './locale';
import { speechParts, useListening } from './listening';
import { OcrPanel } from './ocr-panel';
import { Annotations, type Annotation, type TextSelection } from './annotations';
import type { Appearance } from './appearance';
import { useEffect, useRef, useState } from 'react';
import ePub, { type Rendition } from 'epubjs';
import { getDocument, GlobalWorkerOptions, TextLayer, type PDFDocumentProxy } from 'pdfjs-dist';
import 'pdfjs-dist/web/pdf_viewer.css';
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url';

import { Outline, type OutlineEntry } from './outline';
import { ReaderSearch, searchLimit, textExcerpt, type SearchHit } from './reader-search';
import type Section from 'epubjs/types/section';
import type { NavItem } from 'epubjs/types/navigation';

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

export function EpubReader({ preview, title, bytes, id, initialPosition, appearance }: ReaderProps & { appearance: Appearance }) {
  const tr = useTranslation();
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
  const [annotations, setAnnotations] = useState<Annotation[]>([]);
  const [contents, setContents] = useState<OutlineEntry<string>[]>([]);

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
    rendition.current = view;
    view.hooks.content.register((contents: { document: Document }) => {
      contents.document.addEventListener('keydown', event => {
        if (event.defaultPrevented) return;
        if (event.ctrlKey && event.key.toLowerCase() === 'f') {
          event.preventDefault();
          window.dispatchEvent(new KeyboardEvent('keydown', { key: 'f', ctrlKey: true, cancelable: true }));
          return;
        }
        const delta = continuous ? 0 : pageKeyDelta(event, loadPageTurning());
        if (delta) {
          event.preventDefault();
          void (delta < 0 ? view.prev() : view.next()).catch(() => {});
        }
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
    view.on('relocated', (location: {start: {cfi: string}}) => {
      if (!active) return;
      lastCfi.current = location.start.cfi;
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
        if (active) setReady(true);
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
      rendition.current = null;
      bookRef.current = null;
      book.destroy();
    };
  }, [bytes, id, continuous, preview]);

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
        const candidates = Array.from(body.querySelectorAll('p, li, h1, h2, h3, h4, h5, h6, blockquote, pre'));
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

  return <div onKeyDown={event => {
    if (!ready || continuous) return;
    const delta = pageKeyDelta(event.nativeEvent, loadPageTurning());
    if (delta < 0) void rendition.current?.prev();
    if (delta > 0) void rendition.current?.next();
    if (delta) event.preventDefault();
  }}>
    {error && <p role="alert">{error}</p>}
    <div className="reader-controls">
      <button disabled={!ready} onClick={() => void rendition.current?.prev()}>{tr('上一页', 'Previous page', '前のページ')}</button>
      <button disabled={!ready} onClick={() => void rendition.current?.next()}>{tr('下一页', 'Next page', '次のページ')}</button>
      <label>{tr('阅读方式', 'Reading mode', '閲覧モード')} <select value={continuous ? 'scroll' : 'page'} disabled={!ready} onChange={event => {
        const scrolling = event.target.value === 'scroll';
        savePreference('epub-reading-mode', event.target.value);
        setReady(false);
        setSelection(undefined);
        setError('');
        setContinuous(scrolling);
      }}>
        <option value="page">{tr('左右翻页', 'Pages', 'ページ送り')}</option><option value="scroll">{tr('上下滚动', 'Continuous scroll', '連続スクロール')}</option>
      </select></label>
    </div>
    <details className="contents">
      <summary>{tr('目录', 'Contents', '目次')}</summary>
      <Outline entries={contents} onSelect={target => {
        void rendition.current?.display(target).catch(() => setError('无法打开此目录位置。'));
      }} />
    </details>
    <button disabled={!ready || preparing} onClick={() => void listen()}>{preparing ? tr('正在准备听书…', 'Preparing narration…', '読み上げを準備中…') : tr('听书', 'Listen', '読み上げ')}</button>
    {ready && selection && <div className="reader-controls">
      <button disabled={preparing} onClick={() => void listen(selection.position)}>{tr('从这里听起', 'Read from here', 'ここから読み上げ')}</button>
      <button disabled={preparing} onClick={() => void listen(selection.position, true)}>{tr('从本段听起', 'Read from this paragraph', 'この段落から読み上げ')}</button>
    </div>}
    {ready && <OcrPanel selectedText={selection?.quotation} />}
    {ready && !preview && <Annotations bookId={id} items={annotations} selection={selection} onChange={setAnnotations} onJump={target => {
      void rendition.current?.display(target).catch(() => setError('无法跳转到此标注。'));
    }} />}
    {ready && <ReaderSearch search={search} onSelect={target => {
      void rendition.current?.display(target).catch(() => setError('无法跳转到此结果。'));
    }} />}
    <div ref={container} className="epub-view" aria-label={tr('EPUB 正文', 'EPUB text', 'EPUB 本文')} />
  </div>;
}

type PdfSelection = TextSelection & { page: number; prefixCharacters: number };

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

function PdfCanvas({ document, page, zoom, size, onError, onSelection, annotations }: {
  document: PDFDocumentProxy;
  page: number;
  zoom: string;
  size: { width: number; height: number };
  onError: (message: string) => void;
  onSelection: (selection: PdfSelection) => void;
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
    if (!element || !selection?.rangeCount || selection.isCollapsed) return;
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
      const prefix = range.cloneRange();
      prefix.selectNodeContents(element);
      prefix.setEnd(range.startContainer, range.startOffset);
      const prefixCharacters = prefix.toString().replace(/\s/gu, '').length;
      onSelection({ position, quotation, page, prefixCharacters });
    }
  }

  return <div className="pdf-sheet">
    <canvas ref={canvas} aria-label={'PDF 第 ' + page + ' 页'} />
    <div ref={text} className="textLayer" tabIndex={0} aria-label={'第 ' + page + ' 页正文'} onPointerUp={capture} onKeyUp={capture} />
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
  useEffect(() => {
    if (continuous && surface.current) surface.current.scrollTop = (trackedPage.current - 1) * stride;
  }, [continuous, stride, document]);
  function navigate(target: number, scroll = continuous) {
    if (!document) return;
    const next = Math.max(1, Math.min(target, document.numPages));
    if (surface.current) surface.current.scrollTop = scroll ? (next - 1) * stride : 0;
    setPage(next);
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
      if (!delta || !document) return;
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
  async function listen(from?: PdfSelection, fromParagraph = false) {
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

  return <>
    {error && <p role="alert">{error}</p>}
    <button disabled={!document || preparing} onClick={() => void listen()}>{preparing ? tr('正在准备听书…', 'Preparing narration…', '読み上げを準備中…') : tr('听书', 'Listen', '読み上げ')}</button>
    {selection?.page === page && <div className="reader-controls">
      <button disabled={preparing} onClick={() => void listen(selection)}>{tr('从这里听起', 'Read from here', 'ここから読み上げ')}</button>
      <button disabled={preparing} onClick={() => void listen(selection, true)}>{tr('从本行听起', 'Read from this line', 'この行から読み上げ')}</button>
    </div>}
    <div className="reader-controls">
      <button disabled={!document || page <= 1} onClick={() => navigate(page - 1)}>{tr('上一页', 'Previous page', '前のページ')}</button>
      <output>{document ? page + ' / ' + document.numPages : tr('正在读取…', 'Loading…', '読み込み中…')}</output>
      <button disabled={!document || page >= document.numPages} onClick={() => navigate(page + 1)}>{tr('下一页', 'Next page', '次のページ')}</button>
      <label>{tr('阅读方式', 'Reading mode', '閲覧モード')} <select value={continuous ? 'scroll' : 'page'} onChange={event => {
        const scrolling = event.target.value === 'scroll';
        setContinuous(scrolling);
        savePreference('pdf-reading-mode', scrolling ? 'scroll' : 'page');
        if (!scrolling && surface.current) surface.current.scrollTop = 0;
      }}>
        <option value="page">{tr('左右翻页', 'Pages', 'ページ送り')}</option><option value="scroll">{tr('上下滚动', 'Continuous scroll', '連続スクロール')}</option>
      </select></label>
      <label>{tr('缩放', 'Zoom', '拡大率')} <select value={continuous ? 'page' : zoom} disabled={continuous} onChange={event => setZoom(event.target.value)}>
        <option value="width">{tr('适宽', 'Fit width', '幅に合わせる')}</option>
        <option value="page">{tr('适页', 'Fit page', 'ページに合わせる')}</option>
        {[.75, 1, 1.25, 1.5, 2].map(value =>
          <option key={value} value={value}>{value * 100}%</option>
        )}
      </select></label>
    </div>
    <details className="contents">
      <summary>{tr('目录', 'Contents', '目次')}</summary>
      <Outline entries={contents} onSelect={async target => {
        if (!document || !target) return;
        try {
          const destination = typeof target === 'string' ? await document.getDestination(target) : target;
          const ref = destination?.[0];
          if (typeof ref === 'number') navigate(ref + 1);
          else if (ref && typeof ref === 'object' && 'num' in ref && 'gen' in ref &&
                   typeof ref.num === 'number' && typeof ref.gen === 'number') {
            navigate(await document.getPageIndex({ num: ref.num, gen: ref.gen }) + 1);
          } else {
            setError('无法打开此目录位置。');
          }
        } catch {
          setError('无法打开此目录位置。');
        }
      }} />
    </details>
    {document && !preview && <Annotations bookId={id} items={annotations} selection={selection} onChange={setAnnotations} onJump={position => {
      const mark = readPdfMark(position);
      if (mark) navigate(mark.page);
    }} />}
    {document && <OcrPanel key={page} selectedText={readPdfMark(selection?.position ?? '')?.page === page ? selection?.quotation : undefined} saved={recognized[page]} capture={async () => {
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
    }} />}
    {document && <ReaderSearch onSelect={navigate} search={async (query, signal) => {
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
    }} />}
    <div className="pdf-view" ref={surface} tabIndex={0} aria-label={tr('PDF 阅读区', 'PDF reading area', 'PDF 読書領域')} onScroll={event => {
      if (!continuous || !document) return;
      const target = Math.floor((event.currentTarget.scrollTop + Math.min(120, stride / 2)) / stride) + 1;
      setPage(Math.min(document.numPages, target));
    }}>
      {document && (continuous ? <div className="pdf-scroll-track" style={{ height: document.numPages * stride }}>
        {Array.from({ length: Math.min(document.numPages, page + 2) - Math.max(1, page - 2) + 1 }, (_, index) => {
          const number = Math.max(1, page - 2) + index;
          return <div className="pdf-scroll-slot" key={number} style={{ top: (number - 1) * stride, height: stride }}>
            <PdfCanvas document={document} page={number} zoom="page" size={size} onError={setError} onSelection={setSelection} annotations={annotations} />
          </div>;
        })}
      </div> : <PdfCanvas document={document} page={page} zoom={zoom} size={size} onError={setError} onSelection={setSelection} annotations={annotations} />)}
    </div>
  </>;
}
