import { useTranslation } from './locale';
import { useState } from 'react';
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

export function AppearanceControls({ value, onChange, reading = false }: {
  value: Appearance;
  onChange: (value: Appearance) => void;
  reading?: boolean;
}) {
  const tr = useTranslation();
  const [tab, setTab] = useState<'font' | 'layout' | 'theme'>('font');
  const fontLabels = tr('系统字体|衬线字体|无衬线字体', 'System font|Serif|Sans serif', 'システムフォント|セリフ|サンセリフ').split('|');
  const themeLabels = tr('跟随系统|浅色|护眼|深色', 'System default|Light|Sepia|Dark', 'システム設定|ライト|セピア|ダーク').split('|');
  const font = <label>{tr('字体', 'Font', 'フォント')} <select value={value.font} onChange={event => onChange({ ...value, font: event.target.value as Appearance['font'] })}>
    {fonts.map((name, index) => <option key={name} value={name}>{fontLabels[index]}</option>)}
  </select></label>;
  const layoutFields = <>
    <label>{tr('行距', 'Line spacing', '行間')} <select value={value.lineHeight} onChange={event => onChange({ ...value, lineHeight: Number(event.target.value) })}>
      {lineHeights.map(height => <option key={height}>{height}</option>)}
    </select></label>
    <label>{tr('页边距', 'Page margins', '余白')} <select value={value.margin} onChange={event => onChange({ ...value, margin: Number(event.target.value) })}>
      {margins.map(margin => <option key={margin}>{margin}</option>)}
    </select></label>
  </>;

  if (reading) {
    const position = sizes.indexOf(value.size);
    return <div className="reader-typography">
      <nav className="reader-panel-tabs" aria-label={tr('排版分类', 'Typography options', '文字組みの分類')}>
        <button aria-pressed={tab === 'font'} onClick={() => setTab('font')}>{tr('字体', 'Font', 'フォント')}</button>
        <button aria-pressed={tab === 'layout'} onClick={() => setTab('layout')}>{tr('布局', 'Layout', 'レイアウト')}</button>
        <button aria-pressed={tab === 'theme'} onClick={() => setTab('theme')}>{tr('主题', 'Theme', 'テーマ')}</button>
      </nav>
      <div className="reader-typography-card">
        {tab === 'font' && <>
          <div className="reader-type-size">
            <button disabled={position <= 0} aria-label={tr('减小字号', 'Decrease font size', '文字を小さく')}
              onClick={() => onChange({ ...value, size: sizes[position - 1] })}>A−</button>
            <output>{value.size} px</output>
            <button disabled={position >= sizes.length - 1} aria-label={tr('增大字号', 'Increase font size', '文字を大きく')}
              onClick={() => onChange({ ...value, size: sizes[position + 1] })}>A+</button>
          </div>
          {font}
        </>}
        {tab === 'layout' && layoutFields}
        {tab === 'theme' && <div className="reader-panel-tabs reader-theme-picks">
          {themes.map((theme, index) => <button key={theme} data-pick-theme={theme} aria-pressed={value.theme === theme}
            onClick={() => onChange({ ...value, theme })}>{themeLabels[index]}</button>)}
        </div>}
      </div>
    </div>;
  }

  return <details className="contents">
    <summary>{tr('外观', 'Appearance', '外観')}</summary>
    <div className="reader-controls">
      <label>{tr('主题', 'Theme', 'テーマ')} <select value={value.theme} onChange={event => onChange({ ...value, theme: event.target.value as Appearance['theme'] })}>
        {themes.map((theme, index) => <option key={theme} value={theme}>{themeLabels[index]}</option>)}
      </select></label>
      {font}
      <label>{tr('字号', 'Font size', '文字サイズ')} <select value={value.size} onChange={event => onChange({ ...value, size: Number(event.target.value) })}>
        {sizes.map(size => <option key={size}>{size}</option>)}
      </select></label>
      {layoutFields}
    </div>
  </details>;
}
