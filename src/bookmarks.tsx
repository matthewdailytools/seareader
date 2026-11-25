import { formatOperationError, useTranslation } from './locale';
import { useEffect, useState } from 'react';
import { ReaderSlot, useBookmarkLocationRef, useReaderPanel } from './reader-shell';

export type Bookmark = { id: number; position: string; createdAt: number };

export function Bookmarks({ bookId, onJump }: { bookId: string; onJump: (position: string) => void }) {
  const tr = useTranslation();
  const showReaderPanel = useReaderPanel();
  const location = useBookmarkLocationRef();
  const [items, setItems] = useState<Bookmark[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [status, setStatus] = useState('');

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
    setStatus('');
    try {
      await operation();
      setItems(await window.seaReader.listBookmarks(bookId));
    } catch (reason) {
      setError(formatOperationError(reason, tr));
    } finally {
      setBusy(false);
    }
  }

  async function toggleCurrent() {
    await update(async () => {
      const live = location?.current?.();
      if (live) await window.seaReader.savePosition(bookId, live);
      const position = live ?? await window.seaReader.getPosition(bookId);
      const current = items.find(item => item.position === position);
      if (current) {
        await window.seaReader.removeBookmark(bookId, current.id);
        setStatus(tr('已移除当前书签。', 'Bookmark removed.', 'しおりを削除しました。'));
      } else {
        await window.seaReader.addBookmark(bookId);
        setStatus(tr('已添加当前书签。', 'Bookmark added.', 'しおりを追加しました。'));
      }
    });
  }

  return <>
    <ReaderSlot name="bookmarkAction">
      <button disabled={busy} onClick={() => void toggleCurrent()}>{tr('书签', 'Bookmark', 'しおり')}</button>
      {status && <span className="reader-bookmark-status" role="status">{status}</span>}
      {error && <span className="reader-bookmark-status" role="alert">{error}</span>}
    </ReaderSlot>
    <ReaderSlot name="bookmarks">
      {items.length === 0 ? <p>{tr('暂无书签。', 'No bookmarks yet.', 'しおりはまだありません。')}</p> : <ul>
        {items.map(item => <li key={item.id}>
          <button disabled={busy} onClick={() => {
            showReaderPanel();
            onJump(item.position);
          }}>{new Date(item.createdAt).toLocaleString()}</button>
          <button disabled={busy} onClick={() => void update(() => window.seaReader.removeBookmark(bookId, item.id))}>{tr('删除', 'Delete', '削除')}</button>
        </li>)}
      </ul>}
    </ReaderSlot>
  </>;
}
