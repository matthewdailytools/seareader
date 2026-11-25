import { hydratePreferences, savePreference } from './preferences';
import { defaultPageTurning, loadPageTurning } from './page-turning';
import { BookCover } from './book-cover';
import { formatOperationError, LocaleContext, loadLocalePreference, resolveLocale, translator, loadTranslationTarget, type LocalePreference, type TextLanguage } from './locale';
import { Feeds, type SavedFeed } from './feeds';
import { Downloads, type Download } from './downloads';
import { ListeningProvider, ListeningSettings, useListeningSettings } from './listening';
import type { Annotation } from './annotations';
import { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { EpubReader, PdfReader } from './format-readers';
import { TextReader } from './text-reader';
import { Bookmarks, type Bookmark } from './bookmarks';
import { AppearanceControls, defaultAppearance, loadAppearance } from './appearance';
import './style.css';

type Book = { id: string; title: string; format: 'epub' | 'txt' | 'pdf'; openedAt: number | null };
export type PreviewBook = Pick<Book, 'id' | 'title' | 'format'> & { bytes: Uint8Array };
type OpenResult = { books: Book[]; errors: string[]; openedId?: string; importedCount: number };
declare global {
  interface Window {
    seaReader: {
      translate(id: string, source: string, target: string, text: string, install: boolean): Promise<string>;
      cancelTranslation(id: string): Promise<void>;
      recognizeImage(jobId: string, image: Uint8Array, language: string): Promise<string>;
      cancelRecognition(jobId: string): Promise<void>;
      listRecognizedPages(id: string): Promise<{ page: number; text: string }[]>;
      saveRecognizedPage(id: string, page: number, text: string): Promise<void>;
      listAnnotations(id: string): Promise<Annotation[]>;
      saveAnnotation(id: string, position: string, quotation: string, note: string): Promise<void>;
      removeAnnotation(id: string, annotationId: number): Promise<void>;
      listBookmarks(id: string): Promise<Bookmark[]>;
      addBookmark(id: string): Promise<void>;
      removeBookmark(id: string, bookmarkId: number): Promise<void>;
      generateSpeech(id: string, text: string, language: string): Promise<Uint8Array | null>;
      openVoiceSettings(): Promise<void>;
      cancelSpeech(id: string): Promise<void>;
      getListeningPosition(source: string): Promise<number | null>;
      saveListeningPosition(source: string, part: number): Promise<void>;
      getPosition(id: string): Promise<string | null>;
      savePosition(id: string, position: string): Promise<void>;
      listPreferences(): Promise<{ key: string; value: string }[]>;
      savePreference(key: string, value: string | null): Promise<void>;
      exportData(preferences: object): Promise<boolean>;
      listFeeds(): Promise<SavedFeed[]>;
      fetchFeed(url: string): Promise<{ url: string; xml: string }>;
      saveFeed(url: string, title: string, content: string): Promise<void>;
      removeFeed(url: string): Promise<void>;
      openFeedLink(url: string): Promise<void>;
      previewDownload(id: string): Promise<PreviewBook>;
      fetchAndPreview(url: string, extension?: string): Promise<PreviewBook>;
      getDownloadNetwork(): Promise<'any' | 'wifi'>;
      setDownloadNetwork(mode: 'any' | 'wifi'): Promise<void>;
      listDownloads(): Promise<Download[]>;
      addDownload(url: string, extension?: string): Promise<string>;
      fetchAndImport(url: string, extension?: string): Promise<string>;
      cancelDownload(id: string): Promise<void>;
      retryDownload(id: string): Promise<void>;
      clearCompletedDownloads(): Promise<number>;
      removeDownload(id: string): Promise<void>;
      importDownload(id: string): Promise<string>;
      listBooks(): Promise<Book[]>;
      startupBooks(): Promise<OpenResult>;
      onOpened(listener: (result: OpenResult) => void): () => void;
      importFiles(files: File[]): Promise<OpenResult>;
      chooseBooks(): Promise<OpenResult>;
      readBookAssets(id: string): Promise<Uint8Array>;
      readBook(id: string): Promise<Uint8Array>;
    };
  }
}

function App({ localePreference, setLocalePreference }: {
  localePreference: LocalePreference;
  setLocalePreference: (preference: LocalePreference) => void;
}) {
  const { preferences: listeningPreferences, resetListeningPreferences } = useListeningSettings();
  const tr = translator(resolveLocale(localePreference));
  const [pageTurning, setPageTurning] = useState(loadPageTurning);
  useEffect(() => {
    savePreference('page-turning', JSON.stringify(pageTurning));
  }, [pageTurning]);
  const [translationTarget, setTranslationTarget] = useState(loadTranslationTarget);
  const [appearance, setAppearance] = useState(loadAppearance);
  const [books, setBooks] = useState<Book[]>([]);
  const [selected, setSelected] = useState<Book>();
  const [feedsOpen, setFeedsOpen] = useState(false);
  const [preview, setPreview] = useState<PreviewBook>();
  const [importingPreview, setImportingPreview] = useState(false);
  const [downloadNetwork, setDownloadNetwork] = useState<'any' | 'wifi'>('any');
  useEffect(() => {
    void window.seaReader.getDownloadNetwork().then(setDownloadNetwork, reason => setError(formatOperationError(reason, tr)));
  }, []);
  const [downloadsOpen, setDownloadsOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [sort, setSort] = useState<'recent' | 'title' | 'format'>(() => {
    const current = localStorage.getItem('library-sort');
    return current === 'title' || current === 'format' ? current : 'recent';
  });
  const [exportStatus, setExportStatus] = useState('');
  const [exporting, setExporting] = useState(false);
  const [resumeOnStart, setResumeOnStart] = useState(() => localStorage.getItem('resume-on-start') === 'true');
  const [openAfterImport, setOpenAfterImport] = useState(() => localStorage.getItem('open-after-import') === 'true');
  const [query, setQuery] = useState('');
  const [format, setFormat] = useState('');
  const [jump, setJump] = useState<{ revision: number; position: string }>();
  const [bytes, setBytes] = useState<Uint8Array>();
  const [error, setError] = useState('');

  useEffect(() => {
    document.documentElement.dataset.theme = appearance.theme;
    savePreference('reading-appearance', JSON.stringify(appearance));
  }, [appearance]);

  useEffect(() => {
    function openResult(result: OpenResult, startup = false) {
      setBooks(result.books);
      setError(result.errors.map(error => formatOperationError(error, tr)).join('\n'));
      const book = result.books.find(book => book.id === result.openedId) ??
        (startup && localStorage.getItem('resume-on-start') === 'true'
          ? result.books.find(book => book.openedAt !== null) : undefined);
      if (book) {
        setJump(undefined);
        setBytes(undefined);
        setSelected(book);
      }
    }
    const unsubscribe = window.seaReader.onOpened(openResult);
    window.seaReader.startupBooks().then(result => openResult(result, true), reason => setError(formatOperationError(reason, tr)));
    return unsubscribe;
  }, []);

  useEffect(() => {
    if (!selected) return;
    let active = true;
    window.seaReader.readBook(selected.id).then(
      data => { if (active) setBytes(data); },
      reason => { if (active) setError(formatOperationError(reason, tr)); }
    );
    return () => { active = false; };
  }, [selected]);

  async function chooseBooks(files?: File[]) {
    setError('');
    try {
      const result = await (files ? window.seaReader.importFiles(files) : window.seaReader.chooseBooks());
      if (openAfterImport && result.importedCount === 1) {
        const book = result.books.find(book => book.id === result.openedId);
        if (book) openBook(book);
      }
      setBooks(result.books);
      setError(result.errors.map(error => formatOperationError(error, tr)).join('\n'));
    } catch (reason) {
      setError(formatOperationError(reason, tr));
    }
  }

  function openBook(book: Book) {
    setJump(undefined);
    setBytes(undefined);
    setError('');
    setSelected(book);
  }
  const recent = books.find(book => book.openedAt !== null);
  const filtered = books.filter(book =>
    (!format || book.format === format) &&
    book.title.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase())
  );

  if (sort !== 'recent') {
    filtered.sort((left, right) => left[sort].localeCompare(right[sort]) || left.title.localeCompare(right.title));
  }

  function changeSort(value: 'recent' | 'title' | 'format') {
    setSort(value);
    savePreference('library-sort', value);
  }

  async function changeDownloadNetwork(mode: 'any' | 'wifi') {
    try {
      await window.seaReader.setDownloadNetwork(mode);
      setDownloadNetwork(mode);
    } catch (error) {
      setError(String(error));
    }
  }

  async function exportData() {
    setExporting(true);
    setExportStatus('');
    try {
      if (await window.seaReader.exportData({
        appearance, sort, downloadNetwork, pageTurning, translationTarget, interfaceLanguage: localePreference, resumeOnStart, openAfterImport, listening: listeningPreferences, epubMode: localStorage.getItem('epub-reading-mode') ?? 'page',
        pdfMode: localStorage.getItem('pdf-reading-mode') ?? 'page',
        txtMode: localStorage.getItem('txt-reading-mode') ?? 'scroll'
      })) setExportStatus('已导出阅读数据。');
    } catch {
      setExportStatus('无法导出阅读数据。');
    } finally {
      setExporting(false);
    }
  }

  const libraryMode = !selected && !preview;
  function switchLibraryView(view: 'library' | 'feeds' | 'downloads' | 'settings') {
    setError('');
    setFeedsOpen(view === 'feeds');
    setDownloadsOpen(view === 'downloads');
    setSettingsOpen(view === 'settings');
    if (view === 'library') {
      void window.seaReader.listBooks().then(setBooks, reason => setError(formatOperationError(reason, tr)));
    }
  }

  return <main className={libraryMode ? 'library-shell' : undefined} onDragOver={event => {
    if (event.dataTransfer.types.includes('Files')) event.preventDefault();
  }} onDrop={event => {
    event.preventDefault();
    const files = Array.from(event.dataTransfer.files);
    if (files.length) void chooseBooks(files);
  }}>
    <header>
      <strong>SeaReader</strong>
      <div className="header-actions">
      {preview ? <button onClick={() => { setPreview(undefined); setError(''); }}>{tr('返回来源', 'Back to source', '取得元に戻る')}</button> :
      feedsOpen ? <button onClick={() => setFeedsOpen(false)}>{tr('返回', 'Back', '戻る')}</button> : downloadsOpen ? <button onClick={() => {
        setDownloadsOpen(false);
        void window.seaReader.listBooks().then(setBooks, reason => setError(formatOperationError(reason, tr)));
      }}>{tr('返回', 'Back', '戻る')}</button> : settingsOpen ? <button onClick={() => setSettingsOpen(false)}>{tr('返回', 'Back', '戻る')}</button> : <>
      <button className="compact-nav-action" onClick={() => { setJump(undefined); setFeedsOpen(true); }}>{tr('订阅', 'Subscriptions', '購読')}</button>
      <button className="compact-nav-action" onClick={() => { setJump(undefined); setDownloadsOpen(true); }}>{tr('下载', 'Downloads', 'ダウンロード')}</button>
      <button className="compact-nav-action" onClick={() => { setJump(undefined); setSettingsOpen(true); }}>{tr('设置', 'Settings', '設定')}</button>
      {selected ? <button onClick={() => {
        setSelected(undefined);
        void window.seaReader.listBooks().then(setBooks, reason => setError(formatOperationError(reason, tr)));
      }}>{tr('返回书库', 'Back to library', 'ライブラリに戻る')}</button> :
        <button className="primary" onClick={() => void chooseBooks()}>{tr('导入文件', 'Import files', 'ファイルを取り込む')}</button>}
      </>}
      </div>
    </header>
    {libraryMode && <nav className="library-nav" aria-label={tr('主导航', 'Main navigation', 'メインナビゲーション')}>
      <button aria-current={!feedsOpen && !downloadsOpen && !settingsOpen ? 'page' : undefined}
        onClick={() => switchLibraryView('library')}>{tr('书库', 'Library', 'ライブラリ')}</button>
      <button aria-current={feedsOpen ? 'page' : undefined}
        onClick={() => switchLibraryView('feeds')}>{tr('订阅', 'Subscriptions', '購読')}</button>
      <button aria-current={downloadsOpen ? 'page' : undefined}
        onClick={() => switchLibraryView('downloads')}>{tr('下载', 'Downloads', 'ダウンロード')}</button>
      <button aria-current={settingsOpen ? 'page' : undefined}
        onClick={() => switchLibraryView('settings')}>{tr('设置', 'Settings', '設定')}</button>
    </nav>}
    <div className="workspace-content">
    {error && <p role="alert">{error}</p>}
    {feedsOpen && <div hidden={Boolean(preview)}><Feeds onPreview={setPreview}
      onImported={() => void window.seaReader.listBooks().then(setBooks, reason => setError(formatOperationError(reason, tr)))} /></div>}
    {downloadsOpen && <div hidden={Boolean(preview)}><Downloads onPreview={setPreview} /></div>}
    {preview ? <section className="reading" aria-label={tr('临时阅读', 'Preview', 'プレビュー')}>
      <h1>{preview.title}</h1>
      <button disabled={importingPreview} onClick={async () => {
        setImportingPreview(true);
        setError('');
        try {
          const id = await window.seaReader.importDownload(preview.id);
          const books = await window.seaReader.listBooks();
          setBooks(books);
          const book = books.find(item => item.id === id);
          if (book) {
            setPreview(undefined);
            setFeedsOpen(false);
            setDownloadsOpen(false);
            openBook(book);
          }
        } catch (reason) {
          setError(formatOperationError(reason, tr));
        } finally {
          setImportingPreview(false);
        }
      }}>{tr('导入书库', 'Add to library', 'ライブラリに追加')}</button>
      {preview.format === 'txt' ? <TextReader preview id={preview.id} title={preview.title} bytes={preview.bytes} appearance={appearance} /> :
       preview.format === 'epub' ? <EpubReader preview id={preview.id} title={preview.title} bytes={preview.bytes} appearance={appearance} /> :
       <PdfReader preview id={preview.id} title={preview.title} bytes={preview.bytes} />}
    </section> : feedsOpen || downloadsOpen ? null : settingsOpen ? <section aria-label={tr('设置', 'Settings', '設定')}>
      <h1>{tr('设置', 'Settings', '設定')}</h1>
      <AppearanceControls value={appearance} onChange={setAppearance} layout />
      <p><label>{tr('界面语言', 'Interface language', '表示言語')} <select value={localePreference}
        onChange={event => setLocalePreference(event.target.value as LocalePreference)}>
        <option value="system">{tr('跟随系统', 'System default', 'システム設定')}</option>
        <option value="zh-CN">简体中文</option><option value="en">English</option><option value="ja">日本語</option>
      </select></label></p>
      <ListeningSettings />
      <p><label><input type="checkbox" checked={pageTurning.keyboard}
        onChange={event => setPageTurning({ ...pageTurning, keyboard: event.target.checked })} />
        {tr('使用方向键翻页', 'Turn pages with arrow keys', '矢印キーでページをめくる')}
      </label></p>
      <p><label>{tr('翻页方向', 'Page-turn direction', 'ページ送りの方向')}
        <select value={pageTurning.direction} onChange={event =>
          setPageTurning({ ...pageTurning, direction: event.target.value === 'rtl' ? 'rtl' : 'ltr' })}>
          <option value="ltr">{tr('从左到右', 'Left to right', '左から右')}</option>
          <option value="rtl">{tr('从右到左', 'Right to left', '右から左')}</option>
        </select>
      </label></p>
      <p><label>{tr('默认翻译目标语言', 'Default translation target', '既定の翻訳先言語')}
        <select value={translationTarget} onChange={event => {
          const value = event.target.value as TextLanguage | 'interface';
          savePreference('translation-target', value);
          setTranslationTarget(value);
        }}>
          <option value="interface">{tr('跟随界面语言', 'Match interface language', '表示言語に合わせる')}</option>
          <option value="zh">{tr('简体中文', 'Simplified Chinese', '簡体字中国語')}</option>
          <option value="en">{tr('英语', 'English', '英語')}</option>
          <option value="ja">{tr('日语', 'Japanese', '日本語')}</option>
        </select></label></p>
      <p><label>{tr('下载网络', 'Download network', 'ダウンロード回線')} <select value={downloadNetwork}
        onChange={event => void changeDownloadNetwork(event.target.value === 'wifi' ? 'wifi' : 'any')}>
        <option value="any">{tr('任意网络', 'Any network', 'すべての回線')}</option>
        <option value="wifi">{tr('仅 Wi-Fi', 'Wi-Fi only', 'Wi-Fi のみ')}</option>
      </select></label></p>
      <p><label><input type="checkbox" checked={resumeOnStart} onChange={event => {
        setResumeOnStart(event.target.checked);
        savePreference('resume-on-start', String(event.target.checked));
      }} /> {tr('启动时打开上次阅读', 'Resume on startup', '起動時に前回の本を開く')}</label></p>
      <p><label><input type="checkbox" checked={openAfterImport} onChange={event => {
        setOpenAfterImport(event.target.checked);
        savePreference('open-after-import', String(event.target.checked));
      }} /> {tr('导入单本后直接打开', 'Open after importing one book', '1冊を取り込んだら開く')}</label></p>
      <p><label >{tr('书库排序', 'Sort library', 'ライブラリの並べ替え')} <select value={sort} onChange={event => {
        const value = event.target.value;
        if (value === 'recent' || value === 'title' || value === 'format') changeSort(value);
      }}>
        <option value="recent">{tr('最近阅读', 'Recently read', '最近読んだ本')}</option><option value="title">{tr('书名', 'Title', 'タイトル')}</option><option value="format">{tr('格式', 'Format', '形式')}</option>
      </select></label></p>
      <div className="reader-controls">
        <button disabled={exporting} onClick={() => void exportData()}>{tr('导出阅读数据', 'Export reading data', '読書データを書き出す')}</button>
        <button onClick={() => {
          if (!window.confirm(tr('恢复默认偏好？', 'Reset preferences?', '設定を初期化しますか？'))) return;
          setAppearance(defaultAppearance);
          changeSort('recent');
          resetListeningPreferences();
          void changeDownloadNetwork('any');
          setLocalePreference('system');
          setTranslationTarget('interface');
          setPageTurning(defaultPageTurning);
          savePreference('translation-target', null);
          setResumeOnStart(false);
          setOpenAfterImport(false);
          savePreference('resume-on-start', null);
          savePreference('open-after-import', null);
          savePreference('epub-reading-mode', null);
          savePreference('pdf-reading-mode', null);
          savePreference('txt-reading-mode', null);
        }}>{tr('恢复默认偏好', 'Reset preferences', '設定を初期化')}</button>
      </div>
      {exportStatus && <p role="status">{exportStatus}</p>}
    </section> : selected ? <section className="reading" aria-label={tr('阅读', 'Reading', '読書')}>
      <h1>{selected.title}</h1>
      <AppearanceControls value={appearance} onChange={setAppearance} layout={selected.format !== 'pdf'} />
      {bytes && <Bookmarks key={selected.id} bookId={selected.id} onJump={position => setJump(value => ({ revision: (value?.revision ?? 0) + 1, position }))} />}
      {bytes ? (selected.format === 'txt' ? <TextReader title={selected.title} appearance={appearance} key={jump?.revision ?? 0} initialPosition={jump?.position} id={selected.id} bytes={bytes} /> : selected.format === 'epub' ? <EpubReader title={selected.title} appearance={appearance} key={jump?.revision ?? 0} initialPosition={jump?.position} id={selected.id} bytes={bytes} /> : <PdfReader title={selected.title} key={jump?.revision ?? 0} initialPosition={jump?.position} id={selected.id} bytes={bytes} />) : <p>{tr('正在读取…', 'Loading…', '読み込み中…')}</p>}
    </section> : <section>
      <h1>{tr('书库', 'Library', 'ライブラリ')}</h1>
      {recent && <p><button onClick={() => openBook(recent)}>{tr('继续阅读', 'Continue reading', '続きを読む')} · {recent.title}</button></p>}
      <div className="filters">
        <input type="search" aria-label={tr('搜索书库', 'Search library', 'ライブラリを検索')} placeholder={tr('搜索书名', 'Search titles', 'タイトルを検索')} value={query} onChange={event => setQuery(event.target.value)} />
        <select aria-label={tr('按格式筛选', 'Filter by format', '形式で絞り込む')} value={format} onChange={event => setFormat(event.target.value)}>
          <option value="">{tr('全部格式', 'All formats', 'すべての形式')}</option><option value="epub">EPUB</option>
          <option value="txt">TXT</option><option value="pdf">PDF</option>
        </select>
      </div>
      {filtered.length ? <ul className="shelf">{filtered.map(book => <li key={book.id}>
        <button onClick={() => openBook(book)}>
          <BookCover id={book.id} format={book.format} /><strong>{book.title}</strong>
        </button>
      </li>)}</ul> : <p>{books.length ? tr('没有匹配的书籍。', 'No matching books.', '該当する本がありません。') : tr('导入 EPUB、TXT 或 PDF，开始阅读。', 'Import an EPUB, TXT or PDF to start reading.', 'EPUB、TXT、PDF を取り込んで読書を始めましょう。')}</p>}
    </section>}
    </div>
  </main>;
}

function Root() {
  const [localePreference, setLocalePreference] = useState(loadLocalePreference);
  const locale = resolveLocale(localePreference);
  useEffect(() => {
    document.documentElement.lang = locale;
    savePreference('interface-language', localePreference);
  }, [locale, localePreference]);


  return <LocaleContext.Provider value={locale}>
    <ListeningProvider>
      <App localePreference={localePreference} setLocalePreference={setLocalePreference} />
    </ListeningProvider>
  </LocaleContext.Provider>;
}

const root = createRoot(document.getElementById('root')!);
void hydratePreferences().catch(error => {
  console.error('Could not load preferences', error);
}).finally(() => root.render(<Root />));
