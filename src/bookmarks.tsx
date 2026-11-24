import { formatOperationError, useTranslation } from './locale';
import { useEffect, useState } from 'react';

export type Bookmark = { id: number; position: string; createdAt: number };

export function Bookmarks({ bookId, onJump }: { bookId: string; onJump: (position: string) => void }) {
  const tr = useTranslation();
  const [items, setItems] = useState<Bookmark[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    let active = true;
    window.seaReader.listBookmarks(bookId).then(
      bookmarks => { if (active) setItems(bookmarks); },
      () => { if (active) setError(tr('无法读取书签。', 'Could not load bookmarks.', 'しおりを読み込めませんでした。')); }
    );
    return () => { active = false; };
  }, [bookId]);

  async function update(operation: () => Promise<void>) {
    setBusy(true);
    setError('');
    try {
      await operation();
      setItems(await window.seaReader.listBookmarks(bookId));
    } catch (reason) {
      setError(formatOperationError(reason, tr));
    } finally {
      setBusy(false);
    }
  }

  return <details>
    <summary>{tr('书签', 'Bookmarks', 'しおり')}</summary>
    <button disabled={busy} onClick={() => void update(() => window.seaReader.addBookmark(bookId))}>{tr('添加书签', 'Add bookmark', 'しおりを追加')}</button>
    {error && <p role="alert">{error}</p>}
    {items.length === 0 ? <p>{tr('暂无书签。', 'No bookmarks yet.', 'しおりはまだありません。')}</p> : <ul>
      {items.map(item => <li key={item.id}>
        <button disabled={busy} onClick={() => onJump(item.position)}>{new Date(item.createdAt).toLocaleString()}</button>
        <button disabled={busy} onClick={() => void update(() => window.seaReader.removeBookmark(bookId, item.id))}>{tr('删除', 'Delete', '削除')}</button>
      </li>)}
    </ul>}
  </details>;
}
