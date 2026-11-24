const keys = [
  'reading-appearance', 'listening-preferences', 'interface-language', 'library-sort',
  'resume-on-start', 'open-after-import', 'translation-target', 'page-turning',
  'epub-reading-mode', 'pdf-reading-mode', 'txt-reading-mode'
] as const;
type PreferenceKey = typeof keys[number];

export async function hydratePreferences() {
  const rows = await window.seaReader.listPreferences();
  const current = new Map(rows.map(row => [row.key, row.value]));
  await Promise.all(keys.map(async key => {
    const value = current.get(key);
    if (value !== undefined) {
      localStorage.setItem(key, value);
    } else {
      const previous = localStorage.getItem(key);
      if (previous !== null) await window.seaReader.savePreference(key, previous);
    }
  }));
}

export function savePreference(key: PreferenceKey, value: string | null) {
  if (value === null) localStorage.removeItem(key);
  else localStorage.setItem(key, value);
  void window.seaReader.savePreference(key, value).catch(error => console.error('Could not save preference', error));
}
