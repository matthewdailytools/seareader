import { useEffect, useRef, useState } from 'react';
import ePub from 'epubjs';
import { getDocument } from 'pdfjs-dist';

export function BookCover({ id, format }: { id: string; format: 'epub' | 'txt' | 'pdf' }) {
  const element = useRef<HTMLSpanElement>(null);
  const [cover, setCover] = useState('');

  useEffect(() => {
    if (format === 'txt' || !element.current) return;
    let active = true;
    let dispose: (() => void) | undefined;
    let url: string | undefined;

    async function load() {
      const bytes = await window.seaReader.readBookAssets(id);
      if (!active) return;
      if (format === 'epub') {
        const book = ePub(Uint8Array.from(bytes).buffer);
        dispose = () => book.destroy();
        await book.ready;
        if (!active) return;
        const cover = await book.coverUrl();
        if (active && cover?.startsWith('blob:')) setCover(cover);
        return;
      }
      const task = getDocument({ data: Uint8Array.from(bytes) });
      dispose = () => { void task.destroy().catch(() => {}); };
      const pdf = await task.promise;
      if (!active) return;
      const page = await pdf.getPage(1);
      if (!active) return;
      const native = page.getViewport({ scale: 1 });
      const viewport = page.getViewport({ scale: 240 / Math.max(native.width, native.height) });
      const canvas = document.createElement('canvas');
      canvas.width = Math.ceil(viewport.width);
      canvas.height = Math.ceil(viewport.height);
      await page.render({ canvas, viewport }).promise;
      const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/png'));
      if (!active || !blob) return;
      url = URL.createObjectURL(blob);
      setCover(url);
      dispose?.();
      dispose = undefined;
    }

    const observer = new IntersectionObserver(([entry]) => {
      if (!entry.isIntersecting) return;
      observer.disconnect();
      void load().catch(() => {
        dispose?.();
        dispose = undefined;
      });
    });
    observer.observe(element.current);
    return () => {
      active = false;
      observer.disconnect();
      dispose?.();
      if (url) URL.revokeObjectURL(url);
    };
  }, [id, format]);

  return <span ref={element} className={'book-cover cover-' + format}>
    {cover ? <img src={cover} alt="" onError={() => setCover('')} /> : <span>{format.toUpperCase()}</span>}
  </span>;
}
