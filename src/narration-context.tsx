import { useEffect, useLayoutEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import { useTranslation } from './locale';

export type NarrationMenuPosition = { x: number; y: number; restoreFocus?: () => void };

export function textCursorAtPoint(document: Document, x: number, y: number): Range | undefined {
  const caret = document.caretPositionFromPoint?.(x, y);
  const range = caret ? document.createRange() : document.caretRangeFromPoint?.(x, y);
  const node = caret?.offsetNode ?? range?.startContainer;
  if (!node || !range || node.nodeType !== Node.TEXT_NODE || !node.textContent?.trim()) return;
  const offset = caret?.offset ?? range.startOffset;
  range.setStart(node, Math.min(offset, node.textContent.length - 1));
  range.collapse(true);
  return range;
}

export function narrationMenuKey(event: Pick<KeyboardEvent, 'key' | 'shiftKey'>) {
  return event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10');
}

export function keyboardTextCursor(element: Element): Range | undefined {
  const document = element.ownerDocument;
  const selection = document.getSelection();
  const node = selection?.anchorNode;
  if (selection && node?.nodeType === Node.TEXT_NODE && node.textContent?.trim() && element.contains(node)) {
    const caret = document.createRange();
    caret.setStart(node, Math.min(selection.anchorOffset, node.textContent.length - 1));
    caret.collapse(true);
    return caret;
  }
  const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
  for (let text = walker.nextNode(); text; text = walker.nextNode()) {
    if (text.textContent?.trim() && text.parentElement?.getClientRects().length) {
      const caret = document.createRange();
      caret.setStart(text, 0);
      caret.collapse(true);
      return caret;
    }
  }
}

export function NarrationContextMenu({ position, onClose, onHere, onParagraph, paragraphLabel }: {
  position: NarrationMenuPosition;
  onClose: () => void;
  onHere: () => void;
  onParagraph: () => void;
  paragraphLabel?: string;
}) {
  const tr = useTranslation();
  const menu = useRef<HTMLDivElement>(null);
  const here = useRef<HTMLButtonElement>(null);
  const paragraph = useRef<HTMLButtonElement>(null);

  useLayoutEffect(() => {
    const element = menu.current;
    if (!element) return;
    const { width, height } = element.getBoundingClientRect();
    element.style.left = Math.max(8, Math.min(position.x, window.innerWidth - width - 8)) + 'px';
    element.style.top = Math.max(8, Math.min(position.y, window.innerHeight - height - 8)) + 'px';
    here.current?.focus({ preventScroll: true });
  }, [position]);

  useEffect(() => {
    function outside(event: PointerEvent) {
      if (!menu.current?.contains(event.target as Node)) onClose();
    }
    window.addEventListener('pointerdown', outside, true);
    window.addEventListener('resize', onClose);
    window.addEventListener('scroll', onClose, true);
    return () => {
      window.removeEventListener('pointerdown', outside, true);
      window.removeEventListener('resize', onClose);
      window.removeEventListener('scroll', onClose, true);
    };
  }, [onClose]);

  function closeWithFocus() {
    onClose();
    position.restoreFocus?.();
  }

  return createPortal(<div ref={menu} role="group"
    aria-label={tr('听书起点', 'Narration start position', '読み上げ開始位置')}
    style={{
      position: 'fixed', zIndex: 20, display: 'grid', gap: 4, padding: 6, maxWidth: 'calc(100vw - 16px)',
      background: 'var(--panel)', border: '1px solid var(--edge)', borderRadius: 9, boxShadow: '0 8px 24px #0004'
    }} onKeyDown={event => {
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        closeWithFocus();
      } else if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
        const second = event.key === 'End' || (event.key !== 'Home' && event.target === here.current);
        event.preventDefault();
        (second ? paragraph : here).current?.focus({ preventScroll: true });
      }
    }}>
    <button ref={here} type="button" onClick={() => { closeWithFocus(); onHere(); }}>
      {tr('从这里听起', 'Read from here', 'ここから読み上げ')}
    </button>
    <button ref={paragraph} type="button" onClick={() => { closeWithFocus(); onParagraph(); }}>
      {paragraphLabel ?? tr('从本段听起', 'Read from this paragraph', 'この段落から読み上げ')}
    </button>
  </div>, document.body);
}
