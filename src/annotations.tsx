import { useTranslation } from './locale';
import { useEffect, useState } from 'react';

export type TextSelection = { position: string; quotation: string };
export type Annotation = TextSelection & { id: number; note: string };

export function Annotations({ bookId, selection, items, onChange, onJump }: {
  bookId: string;
  selection?: TextSelection;
  items: Annotation[];
  onChange: (items: Annotation[]) => void;
  onJump: (position: string) => void;
}) {
  const tr = useTranslation();
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [open, setOpen] = useState(false);

  useEffect(() => {
    if (!selection) return;
    setOpen(true);
    setNote(items.find(item => item.position === selection.position)?.note ?? '');
  }, [selection, items]);

  async function update(operation: () => Promise<void>) {
    setBusy(true);
    setError('');
    try {
      await operation();
      onChange(await window.seaReader.listAnnotations(bookId));
    } catch {
      setError(tr('无法保存标注。', 'Could not save the annotation.', '注釈を保存できませんでした。'));
    } finally {
      setBusy(false);
    }
  }

  return <details open={open} onToggle={event => setOpen(event.currentTarget.open)} className="contents">
    <summary>{tr('划线与笔记', 'Highlights and notes', 'ハイライトとメモ')}</summary>
    {selection && <form onSubmit={event => {
      event.preventDefault();
      void update(() => window.seaReader.saveAnnotation(bookId, selection.position, selection.quotation, note));
    }}>
      <blockquote>{selection.quotation}</blockquote>
      <label>{tr('笔记', 'Note', 'メモ')} <textarea value={note} maxLength={12000} onChange={event => setNote(event.target.value)} /></label>
      <button disabled={busy}>{tr('保存划线与笔记', 'Save highlight and note', 'ハイライトとメモを保存')}</button>
    </form>}
    {error && <p role="alert">{error}</p>}
    {items.length ? <ul>{items.map(item => <li key={item.id}>
      <button onClick={() => onJump(item.position)}>{item.quotation.slice(0, 90)}</button>
      {item.note && <p>{item.note}</p>}
      <button disabled={busy} onClick={() => void update(() => window.seaReader.removeAnnotation(bookId, item.id))}>{tr('删除', 'Delete', '削除')}</button>
    </li>)}</ul> : <p>{tr('选择正文可添加划线或笔记。', 'Select text to add a highlight or note.', '本文を選択するとハイライトやメモを追加できます。')}</p>}
  </details>;
}
