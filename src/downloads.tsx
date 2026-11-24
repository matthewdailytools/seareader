import { formatOperationError, useTranslation } from './locale';
import type { PreviewBook } from './app';
import { useEffect, useState } from 'react';

const labels = {
  queued: ['等待', 'Queued', '待機中'],
  running: ['进行中', 'Downloading', 'ダウンロード中'],
  completed: ['完成', 'Complete', '完了'],
  failed: ['失败', 'Failed', '失敗'],
  cancelled: ['已取消', 'Cancelled', 'キャンセル済み']
} as const;
export type Download = { id: string; name: string; url: string; status: keyof typeof labels; error: string | null };

export function Downloads({ onPreview }: { onPreview: (book: PreviewBook) => void }) {
  const tr = useTranslation();
  const [items, setItems] = useState<Download[]>([]);
  const [url, setUrl] = useState('');
  const [status, setStatus] = useState('');
  useEffect(() => {
    let active = true;
    const refresh = async () => {
      try {
        const rows = await window.seaReader.listDownloads();
        if (active) setItems(rows);
      } catch {
        if (active) setStatus(tr('无法读取下载任务。', 'Could not load downloads.', 'ダウンロードを読み込めませんでした。'));
      }
    };
    void refresh();
    const timer = setInterval(refresh, 1000);
    return () => { active = false; clearInterval(timer); };
  }, []);

  async function action(operation: () => Promise<unknown>, message = '') {
    setStatus('');
    try {
      await operation();
      setItems(await window.seaReader.listDownloads());
      setStatus(message);
    } catch (error) {
      setStatus(formatOperationError(error, tr));
    }
  }

  return <section aria-label={tr('下载', 'Downloads', 'ダウンロード')}>
    <h1>{tr('下载', 'Downloads', 'ダウンロード')}</h1>
    <form className="reader-controls" onSubmit={event => {
      event.preventDefault();
      void action(async () => { await window.seaReader.addDownload(url.trim()); setUrl(''); });
    }}>
      <label>{tr('文件链接', 'File URL', 'ファイル URL')} <input value={url} onChange={event => setUrl(event.target.value)} placeholder="https://…" required /></label>
      <button>{tr('添加下载', 'Add download', 'ダウンロードを追加')}</button>
    </form>
    {items.some(item => item.status === 'completed') && <button onClick={() => {
      if (window.confirm(tr('清理已完成的下载副本？', 'Clear completed downloads?', '完了したダウンロードを削除しますか？'))) {
        void action(() => window.seaReader.clearCompletedDownloads());
      }
    }}>{tr('清理已下载副本', 'Clear downloaded copies', 'ダウンロード済みのコピーを削除')}</button>}
    {status && <p role="status">{status}</p>}
    {items.length === 0 ? <p>{tr('暂无下载任务。', 'No downloads yet.', 'ダウンロードはまだありません。')}</p> : <ul className="download-list">
      {items.map(item => <li key={item.id}>
        <strong>{item.name}</strong> · {tr(labels[item.status][0], labels[item.status][1], labels[item.status][2])}
        {item.error && <p>{formatOperationError(item.error, tr)}</p>}
        <div className="reader-controls">
          {['queued', 'running'].includes(item.status)
            ? <button onClick={() => void action(() => window.seaReader.cancelDownload(item.id))}>{tr('取消', 'Cancel', 'キャンセル')}</button>
            : <button onClick={() => void action(() => window.seaReader.removeDownload(item.id))}>{tr('删除', 'Delete', '削除')}</button>}
          {['failed', 'cancelled'].includes(item.status) &&
            <button onClick={() => void action(() => window.seaReader.retryDownload(item.id))}>{tr('重试', 'Retry', '再試行')}</button>}
                    {item.status === 'completed' && <button onClick={() => void action(async () => {
            onPreview(await window.seaReader.previewDownload(item.id));
          })}>{tr('打开', 'Open', '開く')}</button>}
          {item.status === 'completed' &&
            <button onClick={() => void action(() => window.seaReader.importDownload(item.id), tr('已导入书库。', 'Added to the library.', 'ライブラリに追加しました。'))}>{tr('导入书库', 'Add to library', 'ライブラリに追加')}</button>}
        </div>
      </li>)}
    </ul>}
  </section>;
}
