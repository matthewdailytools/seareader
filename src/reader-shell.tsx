import { createContext, useCallback, useContext, useEffect, useId, useMemo, useRef, useState, type ReactNode, type RefObject } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from './locale';

const names = [
  'paging', 'flow', 'listen', 'navigation', 'search', 'tools', 'appearance',
  'selection', 'bookmarks', 'bookmarkAction', 'focusProgress'
] as const;
type SlotName = typeof names[number];
type Panel = 'navigation' | 'search' | 'tools' | 'appearance' | 'bookmarks';
type SlotTargets = Partial<Record<SlotName, HTMLElement | null>>;

const SlotContext = createContext<SlotTargets | null>(null);
const PanelContext = createContext<(panel?: Panel) => void>(() => {});
type GetPosition = () => string | undefined;
const BookmarkLocationContext = createContext<RefObject<GetPosition | null> | null>(null);

export function ReaderSlot({ name, children }: { name: SlotName; children: ReactNode }) {
  const targets = useContext(SlotContext);
  if (!targets) return children;
  const target = targets[name];
  return target ? createPortal(children, target) : null;
}

export function useReaderPanel() {
  return useContext(PanelContext);
}

export function useBookmarkLocation(getPosition: GetPosition) {
  const position = useContext(BookmarkLocationContext);
  useEffect(() => {
    if (!position) return;
    position.current = getPosition;
    return () => {
      if (position.current === getPosition) position.current = null;
    };
  }, [getPosition, position]);
}

export function useBookmarkLocationRef() {
  return useContext(BookmarkLocationContext);
}

export function ReaderShell({ title, format, backLabel, onBack, onSettings, children, extraAction, settings }: {
  title: string;
  format: 'epub' | 'txt' | 'pdf';
  backLabel: string;
  onBack: () => void;
  onSettings: () => void;
  children: ReactNode;
  extraAction?: ReactNode;
  settings?: ReactNode;
}) {
  const tr = useTranslation();
  const [targets, setTargets] = useState<SlotTargets>({});
  const [panel, setPanel] = useState<Panel>();
  const [immersive, setImmersive] = useState(false);
  const bookmarkLocation = useRef<GetPosition | null>(null);
  const aside = useRef<HTMLElement>(null);
  const stage = useRef<HTMLDivElement>(null);
  const settingsPage = useRef<HTMLDivElement>(null);
  const settingsButton = useRef<HTMLButtonElement>(null);
  const wasSettingsOpen = useRef(false);
  const origin = useRef<HTMLElement | null>(null);
  const immersiveToggle = useRef<HTMLButtonElement>(null);
  const showControls = useRef<HTMLButtonElement>(null);
  const pendingFocus = useRef(0);
  const id = useId();
  const usePdfLayout = format === 'pdf';

  const refs = useMemo(() => Object.fromEntries(names.map(name => [
    name,
    (node: HTMLElement | null) => setTargets(previous =>
      previous[name] === node ? previous : { ...previous, [name]: node }
    )
  ])) as Record<SlotName, (node: HTMLElement | null) => void>, []);

  const showPanel = useCallback((value?: Panel) => {
    cancelAnimationFrame(pendingFocus.current);
    if (value && !aside.current?.contains(document.activeElement)) {
      origin.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    }
    if (value) setImmersive(false);
    setPanel(value);
  }, []);

  function closePanel() {
    setPanel(undefined);
    cancelAnimationFrame(pendingFocus.current);
    pendingFocus.current = requestAnimationFrame(() => {
      if (origin.current?.isConnected && origin.current.getClientRects().length) origin.current.focus({ preventScroll: true });
      else stage.current?.focus({ preventScroll: true });
    });
  }

  function togglePanel(value: Panel) {
    if (panel === value) closePanel();
    else showPanel(value);
  }

  function changeImmersive(value: boolean) {
    setPanel(undefined);
    setImmersive(value);
    cancelAnimationFrame(pendingFocus.current);
    pendingFocus.current = requestAnimationFrame(() => (value ? showControls : immersiveToggle).current?.focus({ preventScroll: true }));
  }

  useEffect(() => {
    if (!panel) return;
    const region = targets[panel];
    if (panel === 'search' && region) {
      const searchRegion = region;
      let frame = 0;
      function focusSearch() {
        const search = searchRegion.querySelector<HTMLInputElement>('input[type="search"]');
        if (!search) return false;
        const details = search.closest('details');
        if (details) details.open = true;
        const selectQuery = () => {
          search.focus({ preventScroll: true });
          search.select();
        };
        selectQuery();
        frame = requestAnimationFrame(() => {
          if (document.activeElement === document.body || stage.current?.contains(document.activeElement)) selectQuery();
        });
        return true;
      }
      if (focusSearch()) return () => cancelAnimationFrame(frame);
      aside.current?.focus({ preventScroll: true });
      const observer = new MutationObserver(() => {
        if ((document.activeElement === aside.current || document.activeElement === document.body) && focusSearch()) {
          observer.disconnect();
        }
      });
      observer.observe(searchRegion, { childList: true, subtree: true });
      return () => { observer.disconnect(); cancelAnimationFrame(frame); };
    }
    const details = region?.querySelector('details');
    if (details && panel !== 'tools') details.open = true;
    aside.current?.focus({ preventScroll: true });
  }, [panel, targets]);

  const settingsOpen = Boolean(settings);
  useEffect(() => {
    if (settingsOpen) {
      cancelAnimationFrame(pendingFocus.current);
      settingsPage.current?.focus({ preventScroll: true });
    }
    else if (wasSettingsOpen.current) settingsButton.current?.focus({ preventScroll: true });
    wasSettingsOpen.current = settingsOpen;
  }, [settingsOpen]);
  useEffect(() => () => cancelAnimationFrame(pendingFocus.current), []);

  useEffect(() => {
    if (settingsOpen) return;
    function keyDown(event: KeyboardEvent) {
      const key = event.key.toLowerCase();
      const find = event.ctrlKey && key === 'f';
      if (event.defaultPrevented && !find) return;
      if (find) {
        event.preventDefault();
        if (panel === 'search') {
          const input = targets.search?.querySelector<HTMLInputElement>('input[type="search"]');
          input?.focus();
          input?.select();
        } else showPanel('search');
      } else if (event.key === 'Escape' && (panel || immersive)) {
        event.preventDefault();
        if (panel) closePanel();
        else changeImmersive(false);
      } else if (key === 'f' && !panel && !event.altKey && !event.metaKey && !event.ctrlKey &&
          !(event.target instanceof Element && event.target.closest('input, textarea, select, [contenteditable]'))) {
        event.preventDefault();
        changeImmersive(!immersive);
      }
    }
    window.addEventListener('keydown', keyDown);
    return () => window.removeEventListener('keydown', keyDown);
  }, [panel, immersive, settingsOpen, showPanel, targets]);

  const labels = {
    navigation: usePdfLayout ? tr('页码', 'Pages', 'ページ') : tr('目录', 'Contents', '目次'),
    search: tr('搜索', 'Search', '検索'),
    bookmarks: tr('书签', 'Bookmarks', 'しおり'),
    appearance: usePdfLayout ? tr('页面', 'Page view', 'ページ表示') : tr('排版', 'Typography', '文字組み'),
    tools: tr('阅读工具', 'Reading tools', '読書ツール')
  };

  function panelButton(value: Panel) {
    return <button type="button" aria-controls={id + '-panel'} aria-expanded={panel === value}
      aria-keyshortcuts={value === 'search' ? 'Control+F' : undefined} onClick={() => togglePanel(value)}>
      {labels[value]}
    </button>;
  }

  return <SlotContext.Provider value={targets}>
    <BookmarkLocationContext.Provider value={bookmarkLocation}>
    <PanelContext.Provider value={showPanel}>
      <section className="reader-shell" aria-label={extraAction
        ? tr('临时阅读', 'Preview', 'プレビュー')
        : tr('阅读', 'Reading', '読書')}>
        <div className={'reader-view' + (immersive ? ' reader-immersive' : '')} inert={settingsOpen}>
          <header className="reader-header" hidden={immersive}>
            <button type="button" onClick={onBack}>‹ {backLabel}</button>
            <h1 title={title}>{title}</h1>
            {panelButton('search')}
            <button type="button" ref={immersiveToggle} aria-keyshortcuts="F" onClick={() => changeImmersive(true)}>
              {tr('沉浸', 'Focus', '集中表示')}
            </button>
            <button type="button" ref={settingsButton} onClick={onSettings}>{tr('设置', 'Settings', '設定')}</button>
          </header>
          <div className="reader-workspace" data-panel={panel}>
            <div ref={stage} className={'reader-stage reader-stage-' + format} tabIndex={-1}>
              <div className="reader-paper">{children}</div>
              <div ref={refs.selection} className="reader-selection" hidden={immersive} />
            </div>
            <aside ref={aside} className="reader-panel" id={id + '-panel'} tabIndex={-1}
              aria-label={panel && labels[panel]} hidden={!panel || immersive}>
              <div className="reader-panel-heading">
                <h2>{panel && labels[panel]}</h2>
                <button type="button" onClick={closePanel}>{tr('关闭', 'Close', '閉じる')}</button>
              </div>
              {(panel === 'navigation' || panel === 'bookmarks') && !extraAction &&
                <nav className="reader-panel-tabs" aria-label={tr('书内位置', 'Book locations', '本の位置')}>
                  {panelButton('navigation')}{panelButton('bookmarks')}
                </nav>}
              <div ref={refs.navigation} hidden={panel !== 'navigation'} />
              <div ref={refs.search} hidden={panel !== 'search'} />
              <div ref={refs.bookmarks} hidden={panel !== 'bookmarks'} />
              <div ref={refs.appearance} hidden={panel !== 'appearance'} />
              <div ref={refs.tools} hidden={panel !== 'tools'} />
            </aside>
          </div>
          <footer className="reader-footer" hidden={immersive}>
            <div ref={refs.paging} className="reader-paging" />
            <div className="reader-actions">
              {panelButton('navigation')}
              {extraAction ?? <div ref={refs.bookmarkAction} className="reader-action-slot" />}
              {panelButton('appearance')}
              <div ref={refs.flow} className="reader-action-slot" />
              <div ref={refs.listen} className="reader-action-slot" />
              {panelButton('tools')}
            </div>
          </footer>
          <div className="reader-focus-status" hidden={!immersive}>
            <span ref={refs.focusProgress} />
            <button type="button" ref={showControls} aria-keyshortcuts="F Escape" onClick={() => changeImmersive(false)}>
              {tr('显示工具', 'Show controls', '操作を表示')}
            </button>
          </div>
        </div>
        {settingsOpen && <div ref={settingsPage} className="reader-settings-page" tabIndex={-1}>{settings}</div>}
      </section>
    </PanelContext.Provider>
    </BookmarkLocationContext.Provider>
  </SlotContext.Provider>;
}
