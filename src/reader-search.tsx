import { useTranslation } from './locale';
import { useEffect, useRef, useState } from 'react';

export type SearchHit<T> = { target: T; excerpt: string };
export const searchLimit = 100;

export function ReaderSearch<T>({ search, onSelect }: {
  search: (query: string, signal: AbortSignal) => Promise<SearchHit<T>[]>;
  onSelect: (target: T) => void;
}) {
  const tr = useTranslation();
  const [query, setQuery] = useState('');
  const [hits, setHits] = useState<SearchHit<T>[]>();
  const [status, setStatus] = useState('');
  const input = useRef<HTMLInputElement>(null);
  const panel = useRef<HTMLDetailsElement>(null);
  const pending = useRef<AbortController | null>(null);

  useEffect(() => {
    function onKey(event: KeyboardEvent) {
      if (!(event.ctrlKey && event.key.toLowerCase() === 'f')) return;
      event.preventDefault();
      if (panel.current) panel.current.open = true;
      input.current?.focus();
      input.current?.select();
    }
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('keydown', onKey);
      pending.current?.abort();
    };
  }, []);

  async function run() {
    pending.current?.abort();
    const value = query.trim();
    setHits(undefined);
    if (!value) { setStatus(''); return; }
    const request = new AbortController();
    pending.current = request;
    setStatus(tr('正在查找…', 'Searching…', '検索中…'));
    try {
      const results = await search(value, request.signal);
      if (request.signal.aborted) return;
      setHits(results);
      setStatus(results.length ? '找到 ' + results.length + (results.length === searchLimit ? '+ 个结果' : ' 个结果') : tr('没有匹配结果。', 'No matches.', '一致する項目がありません。'));
    } catch {
      if (!request.signal.aborted) setStatus(tr('无法搜索此书。', 'Could not search this book.', 'この本を検索できませんでした。'));
    }
  }

  return <details ref={panel} className="contents">
    <summary>{tr('查找', 'Find', '検索')}</summary>
    <form className="reader-controls" onSubmit={event => { event.preventDefault(); void run(); }}>
      <input ref={input} type="search" aria-label={tr('搜索正文', 'Search text', '本文を検索')} value={query} onChange={event => {
        pending.current?.abort();
        setQuery(event.target.value);
        setHits(undefined);
        setStatus('');
      }} />
      <button>{tr('查找', 'Find', '検索')}</button>
    </form>
    {status && <p role="status">{status}</p>}
    {hits?.length ? <ul>{hits.map((hit, index) => <li key={index}>
      <button onClick={() => onSelect(hit.target)}>{hit.excerpt}</button>
    </li>)}</ul> : null}
  </details>;
}

export function textExcerpt(text: string, index: number, length: number) {
  const start = Math.max(0, index - 28);
  const end = Math.min(text.length, index + length + 45);
  return (start ? '…' : '') + text.slice(start, end) + (end < text.length ? '…' : '');
}
