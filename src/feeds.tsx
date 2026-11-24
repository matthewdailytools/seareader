import { formatOperationError, useTranslation } from './locale';
import type { PreviewBook } from './app';
import { useEffect, useState } from 'react';

type Link = { url: string; label?: string; extension?: string; action: 'download' | 'catalog' | 'web' };
type Catalog = { title: string; entries: { title: string; links: Link[] }[]; next?: string };
export type SavedFeed = { url: string; title: string; content: string };

function parseFeed(xml: string, base: string): Catalog {
  const root = new DOMParser().parseFromString(xml, 'application/xml').documentElement;
  const children = (element: Element, name: string) => Array.from(element.children).filter(child => child.localName === name);
  const text = (element: Element, name: string) => children(element, name)[0]?.textContent?.trim();
  function address(raw: string | null | undefined, element: Element) {
    if (!raw) return;
    try {
      const ancestors: Element[] = [];
      for (let node: Element | null = element; node; node = node.parentElement) ancestors.unshift(node);
      let resolved = base;
      for (const node of ancestors) {
        const override = node.getAttributeNS('http://www.w3.org/XML/1998/namespace', 'base');
        if (override) resolved = new URL(override, resolved).href;
      }
      const url = new URL(raw, resolved);
      if (['http:', 'https:', 'ftp:'].includes(url.protocol) && !url.username && !url.password) return url.href;
    } catch { /* Ignore invalid entry links. */ }
  }
  function makeLink(element: Element, raw: string | null | undefined, action: Link['action']): Link[] {
    const url = address(raw, element);
    if (!url || (action !== 'download' && url.startsWith('ftp:'))) return [];
    const mime = element.getAttribute('type')?.split(';')[0];
    const format = mime === 'application/epub+zip' ? 'EPUB' : mime === 'application/pdf' ? 'PDF' :
      mime === 'text/plain' ? 'TXT' : new URL(url).pathname.match(/\.(epub|pdf|txt)$/i)?.[1]?.toUpperCase();
    const label = element.getAttribute('title') || undefined;
    return [{ url, label, extension: format?.toLowerCase(), action }];
  }

  if (root.localName === 'rss') {
    const channel = children(root, 'channel')[0];
    if (!channel) throw new Error('RSS 书源缺少频道。');
    return {
      title: text(channel, 'title') || base,
      entries: children(channel, 'item').map(item => ({
        title: text(item, 'title') || '未命名条目',
        links: [
          ...children(item, 'enclosure').flatMap(link => makeLink(link, link.getAttribute('url'), 'download')),
          ...children(item, 'link').flatMap(link => makeLink(link, link.textContent?.trim(), 'web'))
        ]
      }))
    };
  }
  if (root.localName !== 'feed') throw new Error('无法读取书源，请提供 RSS 或 OPDS 地址。');
  const next = children(root, 'link').find(link => link.getAttribute('rel') === 'next');
  return {
    title: text(root, 'title') || base,
    next: next ? address(next.getAttribute('href'), next) : undefined,
    entries: children(root, 'entry').map(entry => ({
      title: text(entry, 'title') || '未命名条目',
      links: children(entry, 'link').flatMap(link => {
        const rel = link.getAttribute('rel') ?? 'alternate';
        const type = link.getAttribute('type') ?? '';
        const action = rel.startsWith('http://opds-spec.org/acquisition') ? 'download' :
          type.includes('application/atom+xml') ? 'catalog' : rel === 'alternate' ? 'web' : undefined;
        return action ? makeLink(link, link.getAttribute('href'), action) : [];
      })
    }))
  };
}

export function Feeds({ onImported, onPreview }: { onImported: () => void; onPreview: (book: PreviewBook) => void }) {
  const tr = useTranslation();
  const [feeds, setFeeds] = useState<SavedFeed[]>([]);
  const [selected, setSelected] = useState<string>();
  const [trail, setTrail] = useState<Catalog[]>([]);
  const [input, setInput] = useState('');
  const [status, setStatus] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void window.seaReader.listFeeds().then(setFeeds, () => setStatus(tr('无法读取订阅。', 'Could not load subscriptions.', '購読を読み込めませんでした。')));
  }, []);

  const feed = feeds.find(item => item.url === selected);
  const catalog: Catalog | undefined = trail.at(-1) ?? (feed ? JSON.parse(feed.content) : undefined);

  async function act(operation: () => Promise<void>) {
    setBusy(true);
    setStatus('');
    try { await operation(); }
    catch (error) { setStatus(formatOperationError(error, tr)); }
    finally { setBusy(false); }
  }

  async function load(url: string) {
    const response = await window.seaReader.fetchFeed(url);
    return parseFeed(response.xml, response.url);
  }

  async function subscribe(url: string) {
    const content = await load(url);
    await window.seaReader.saveFeed(url, content.title, JSON.stringify(content));
    setFeeds(await window.seaReader.listFeeds());
    setTrail([]);
    setSelected(new URL(url).href.split('#')[0]);
    setInput('');
  }

  function follow(url: string) {
    void act(async () => {
      const next = await load(url);
      setTrail(current => [...current, next]);
    });
  }

  return <section aria-label={tr('订阅', 'Subscriptions', '購読')}>
    <h1>{tr('订阅', 'Subscriptions', '購読')}</h1>
    <form className="reader-controls" onSubmit={event => {
      event.preventDefault();
      void act(() => subscribe(input.trim()));
    }}>
      <label>{tr('书源地址', 'Feed URL', 'フィード URL')} <input type="url" value={input} onChange={event => setInput(event.target.value)} required /></label>
      <button disabled={busy}>{tr('添加订阅', 'Add subscription', '購読を追加')}</button>
    </form>
    <div className="reader-controls">{feeds.map(item => <button key={item.url} disabled={busy} aria-pressed={item.url === selected}
      onClick={() => { setSelected(item.url); setTrail([]); setStatus(''); }}>{item.title}</button>)}</div>
    {feeds.length > 0 && <button disabled={busy} onClick={() => void act(async () => {
      let failures = 0;
      for (const item of feeds) {
        try {
          const content = await load(item.url);
          await window.seaReader.saveFeed(item.url, content.title, JSON.stringify(content));
        } catch {
          failures++;
        }
      }
      setFeeds(await window.seaReader.listFeeds());
      setStatus(failures ? tr(failures + ' 个订阅刷新失败，已保留上次内容。',
        failures + ' subscriptions failed to refresh. Previous entries were retained.',
        failures + ' 件の購読を更新できなかったため、以前の内容を保持しました。') :
        tr('已刷新全部订阅。', 'All subscriptions refreshed.', 'すべての購読を更新しました。'));
    })}>{tr('刷新全部订阅', 'Refresh all subscriptions', 'すべての購読を更新')}</button>}
    {(busy || status) && <p role="status">{busy ? tr('处理中…', 'Working…', '処理中…') : status}</p>}
    {feed && catalog ? <>
      <div className="reader-controls">
        {trail.length > 0 && <button disabled={busy} onClick={() => setTrail(trail.slice(0, -1))}>{tr('返回上级', 'Parent catalog', '上の階層に戻る')}</button>}
        <button disabled={busy} onClick={() => void act(() => subscribe(feed.url))}>{tr('刷新订阅', 'Refresh subscription', '購読を更新')}</button>
        <button disabled={busy} onClick={() => void act(async () => {
          await window.seaReader.removeFeed(feed.url);
          setFeeds(await window.seaReader.listFeeds());
          setSelected(undefined);
          setTrail([]);
        })}>{tr('取消订阅', 'Unsubscribe', '購読を解除')}</button>
      </div>
      <h2>{catalog.title}</h2>
      {catalog.entries.length ? <ul>{catalog.entries.map((item, index) => <li key={index}>
        <strong>{item.title}</strong>
        <div className="reader-controls">{item.links.map((link, index) =>
          <span key={index}><button disabled={busy} onClick={() => {
            if (link.action === 'catalog') follow(link.url);
            else if (link.action === 'web') void act(() => window.seaReader.openFeedLink(link.url));
            else void act(async () => {
              await window.seaReader.addDownload(link.url, link.extension);
              setStatus(tr('已加入下载队列。', 'Added to the download queue.', 'ダウンロードに追加しました。'));
            });
          }}>{link.label && !/^(?:(?:EPUB|PDF|TXT) )?下载$|^打开(?:目录|网页)$/.test(link.label) ? link.label :
            link.action === 'catalog' ? tr('打开目录', 'Open catalog', 'カタログを開く') :
            link.action === 'web' ? tr('打开网页', 'Open website', 'ウェブサイトを開く') :
            [link.extension?.toUpperCase(), tr('下载', 'Download', 'ダウンロード')].filter(Boolean).join(' ')
          }</button>
          {link.action === 'download' && <>
          <button disabled={busy} onClick={() => void act(async () => {
            onPreview(await window.seaReader.fetchAndPreview(link.url, link.extension));
          })}>{tr('打开', 'Open', '開く')}</button>
          <button disabled={busy} onClick={() => void act(async () => {
            await window.seaReader.fetchAndImport(link.url, link.extension);
            onImported();
            setStatus(tr('已导入书库。', 'Added to the library.', 'ライブラリに追加しました。'));
          })}>{tr('导入书库', 'Add to library', 'ライブラリに追加')}</button></>}
          </span>
        )}</div>
      </li>)}</ul> : <p>{tr('此书源没有条目。', 'This feed has no entries.', 'このフィードに項目はありません。')}</p>}
      {catalog.next && <button disabled={busy} onClick={() => follow(catalog.next!)}>{tr('下一页', 'Next page', '次のページ')}</button>}
    </> : <p>{feeds.length ? tr('选择一个订阅。', 'Select a subscription.', '購読を選択してください。') : tr('添加 RSS 或 OPDS 书源。', 'Add an RSS or OPDS feed.', 'RSS または OPDS フィードを追加してください。')}</p>}
  </section>;
}
