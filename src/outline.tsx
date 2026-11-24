import { useTranslation } from './locale';
export type OutlineEntry<T> = {
  title: string;
  target: T;
  children?: OutlineEntry<T>[];
};

export function Outline<T>({ entries, onSelect }: {
  entries: OutlineEntry<T>[];
  onSelect: (target: T) => void;
}) {
  const tr = useTranslation();
  if (!entries.length) return <p>{tr('此文件没有目录。', 'This file has no table of contents.', 'このファイルには目次がありません。')}</p>;
  return <ul>{entries.map((entry, index) => <li key={index}>
    <button onClick={() => onSelect(entry.target)}>{entry.title}</button>
    {entry.children?.length ? <Outline entries={entry.children} onSelect={onSelect} /> : null}
  </li>)}</ul>;
}
