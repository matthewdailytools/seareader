export type PageTurningPreferences = { keyboard: boolean; direction: 'ltr' | 'rtl' };

export const defaultPageTurning: PageTurningPreferences = { keyboard: true, direction: 'ltr' };

export function loadPageTurning(): PageTurningPreferences {
  try {
    const saved = JSON.parse(localStorage.getItem('page-turning') ?? '{}');
    return { keyboard: saved.keyboard !== false, direction: saved.direction === 'rtl' ? 'rtl' : 'ltr' };
  } catch {
    return defaultPageTurning;
  }
}

export function pageKeyDelta(event: KeyboardEvent, preferences: PageTurningPreferences): number {
  if (!preferences.keyboard || event.defaultPrevented || event.altKey || event.ctrlKey ||
      event.metaKey || event.shiftKey) return 0;
  const target = event.target as Element | null;
  if (target?.closest?.('button, input, select, textarea, [contenteditable]')) return 0;
  const delta = event.key === 'ArrowLeft' ? -1 : event.key === 'ArrowRight' ? 1 : 0;
  return preferences.direction === 'rtl' ? -delta : delta;
}
