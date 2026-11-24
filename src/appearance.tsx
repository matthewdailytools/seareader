import { useTranslation } from './locale';
export const themes = ['system', 'light', 'sepia', 'dark'] as const;
const fonts = ['system-ui', 'Georgia', 'sans-serif'] as const;
const sizes = [14, 16, 18, 20, 22, 24, 28, 32];
const lineHeights = [1.4, 1.6, 1.8, 2, 2.2];
const margins = [16, 24, 32, 40];

export type Appearance = {
  theme: typeof themes[number];
  font: typeof fonts[number];
  size: number;
  lineHeight: number;
  margin: number;
};

export const defaultAppearance: Appearance = {
  theme: 'system', font: 'system-ui', size: 18, lineHeight: 1.8, margin: 24
};

export function loadAppearance(): Appearance {
  try {
    const saved = JSON.parse(localStorage.getItem('reading-appearance') ?? 'null');
    if (!saved) return defaultAppearance;
    return {
      theme: themes.includes(saved.theme) ? saved.theme : defaultAppearance.theme,
      font: fonts.includes(saved.font) ? saved.font : defaultAppearance.font,
      size: sizes.includes(saved.size) ? saved.size : defaultAppearance.size,
      lineHeight: lineHeights.includes(saved.lineHeight) ? saved.lineHeight : defaultAppearance.lineHeight,
      margin: margins.includes(saved.margin) ? saved.margin : defaultAppearance.margin
    };
  } catch {
    return defaultAppearance;
  }
}

export function AppearanceControls({ value, onChange, layout }: {
  value: Appearance;
  onChange: (value: Appearance) => void;
  layout: boolean;
}) {
  const tr = useTranslation();
  return <details className="contents">
    <summary>{tr('外观', 'Appearance', '外観')}</summary>
    <div className="reader-controls">
      <label>{tr('主题', 'Theme', 'テーマ')} <select value={value.theme} onChange={event => onChange({ ...value, theme: event.target.value as Appearance['theme'] })}>
        {themes.map((theme, index) => <option key={theme} value={theme}>{tr(['跟随系统', '浅色', '护眼', '深色'][index], ['System default', 'Light', 'Sepia', 'Dark'][index], ['システム設定', 'ライト', 'セピア', 'ダーク'][index])}</option>)}
      </select></label>
      {layout && <>
        <label>{tr('字体', 'Font', 'フォント')} <select value={value.font} onChange={event => onChange({ ...value, font: event.target.value as Appearance['font'] })}>
          {fonts.map((font, index) => <option key={font} value={font}>{tr(['系统字体', '衬线字体', '无衬线字体'][index], ['System font', 'Serif', 'Sans serif'][index], ['システムフォント', 'セリフ', 'サンセリフ'][index])}</option>)}
        </select></label>
        <label>{tr('字号', 'Font size', '文字サイズ')} <select value={value.size} onChange={event => onChange({ ...value, size: Number(event.target.value) })}>
          {sizes.map(size => <option key={size}>{size}</option>)}
        </select></label>
        <label>{tr('行距', 'Line spacing', '行間')} <select value={value.lineHeight} onChange={event => onChange({ ...value, lineHeight: Number(event.target.value) })}>
          {lineHeights.map(height => <option key={height}>{height}</option>)}
        </select></label>
        <label>{tr('页边距', 'Page margins', '余白')} <select value={value.margin} onChange={event => onChange({ ...value, margin: Number(event.target.value) })}>
          {margins.map(margin => <option key={margin}>{margin}</option>)}
        </select></label>
      </>}
    </div>
  </details>;
}
