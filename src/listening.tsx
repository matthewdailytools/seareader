import { formatOperationError, useTranslation, detectTextLanguage } from './locale';
import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react';

export type ListeningSource = { id: string; title: string; parts: string[]; chapters?: number[] };
type Start = (source: ListeningSource, index?: number) => void;
const speedOptions = [.75, 1, 1.25, 1.5, 2];
const speechLanguages = { auto: '自动', zh: '简体中文', en: '英语', ja: '日语' };
type SpeechLanguage = keyof typeof speechLanguages;
type ListeningPreferences = { rate: number; language: SpeechLanguage };
const defaults: ListeningPreferences = { rate: 1, language: 'auto' };

export function loadListeningPreferences(): ListeningPreferences {
  try {
    const saved = JSON.parse(localStorage.getItem('listening-preferences') ?? '{}');
    return {
      rate: speedOptions.includes(saved.rate) ? saved.rate : defaults.rate,
      language: Object.hasOwn(speechLanguages, saved.language) ? saved.language : defaults.language
    };
  } catch {
    return defaults;
  }
}

const ListeningSettingsContext = createContext({
  preferences: defaults,
  setPreferences: (_value: ListeningPreferences) => {}
});

export function ListeningSettings() {
  const tr = useTranslation();
  const { preferences, setPreferences } = useContext(ListeningSettingsContext);
  return <div className="reader-controls">
    <label>{tr('默认听书倍速', 'Default narration speed', '既定の読み上げ速度')} <select value={preferences.rate} onChange={event =>
      setPreferences({ ...preferences, rate: Number(event.target.value) })}>
      {speedOptions.map(value => <option key={value} value={value}>{value}×</option>)}
    </select></label>
    <label>{tr('听书语言', 'Narration language', '読み上げ言語')} <select value={preferences.language} onChange={event =>
      setPreferences({ ...preferences, language: event.target.value as SpeechLanguage })}>
      {Object.entries(speechLanguages).map(([value, label], index) => <option key={value} value={value}>
        {tr(label, ['Automatic', 'Chinese', 'English', 'Japanese'][index], ['自動', '中国語', '英語', '日本語'][index])}
      </option>)}
    </select></label>
  </div>;
}

export function useListeningSettings() {
  const { preferences, setPreferences } = useContext(ListeningSettingsContext);
  return { preferences, resetListeningPreferences: () => setPreferences(defaults) };
}

const ListeningContext = createContext<Start>(() => {});
export const useListening = () => useContext(ListeningContext);

export function speechParts(text: string): string[] {
  return text.split(/(?<=[。！？.!?])\s*|\n+/u).flatMap(part =>
    part.trim().match(/[\s\S]{1,180}/gu) ?? []
  ).filter(part => part.trim());
}

export function ListeningProvider({ children }: { children: ReactNode }) {
  const tr = useTranslation();
  const [source, setSource] = useState<ListeningSource>();
  const [index, setIndex] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [preferences, setPreferences] = useState(loadListeningPreferences);
  const [rate, setRate] = useState(preferences.rate);

  function updatePreferences(value: ListeningPreferences) {
    setPreferences(value);
    setRate(value.rate);
    localStorage.setItem('listening-preferences', JSON.stringify(value));
  }
  const [error, setError] = useState('');

  const request = useRef(0);
  const audio = useRef<HTMLAudioElement>(null);
  const [clip, setClip] = useState('');

  function start(next: ListeningSource, position?: number) {
    if (!next.parts.length) return;
    const current = ++request.current;
    async function open() {
      let saved = position;
      let warning = '';
      if (saved === undefined && next.id) {
        try {
          saved = await window.seaReader.getListeningPosition(next.id) ??
            Number(localStorage.getItem('listening:' + next.id));
        } catch {
          warning = tr('无法读取听书进度。', 'Could not load narration progress.', '読み上げ位置を読み込めませんでした。');
        }
      }
      if (request.current !== current) return;
      setError(warning);
      setSource(next);
      setRate(preferences.rate);
      setIndex(Math.max(0, Math.min(saved !== undefined && Number.isSafeInteger(saved) ? saved : 0, next.parts.length - 1)));
      setPlaying(true);
    }
    void open();
  }

  useEffect(() => {
    if (!source?.id) return;
    void window.seaReader.saveListeningPosition(source.id, index).catch(() => setError(tr('无法保存听书进度。', 'Could not save narration progress.', '読み上げ位置を保存できませんでした。')));
  }, [source, index]);

  useEffect(() => {
    if (!source) return;
    const text = source.parts[index];
    const id = crypto.randomUUID();
    let active = true;
    let url: string | undefined;
    setClip('');
    void window.seaReader.generateSpeech(id, text, preferences.language === 'auto' ? detectTextLanguage(text) : preferences.language).then(bytes => {
      if (!active) return;
      url = URL.createObjectURL(new Blob([Uint8Array.from(bytes)], { type: 'audio/wav' }));
      setClip(url);
    }, reason => {
      if (!active) return;
      setPlaying(false);
      setError(formatOperationError(reason, tr));
    });
    return () => {
      active = false;
      if (url) URL.revokeObjectURL(url);
      else void window.seaReader.cancelSpeech(id).catch(() => {});
    };
  }, [source, index, preferences.language]);

  useEffect(() => {
    const player = audio.current;
    if (!player) return;
    player.playbackRate = rate;
    if (playing && clip) {
      let active = true;
      void player.play().catch(() => {
        if (active) { setPlaying(false); setError(tr('无法播放本地语音。', 'Could not play the local voice.', '端末の音声を再生できませんでした。')); }
      });
      return () => { active = false; };
    }
    player.pause();
  }, [clip, playing, rate]);

  useEffect(() => {
    if (!source || !('mediaSession' in navigator)) return;
    const session = navigator.mediaSession;
    session.metadata = new MediaMetadata({ title: source.title, artist: 'SeaReader' });
    session.setActionHandler('play', () => setPlaying(true));
    session.setActionHandler('pause', () => setPlaying(false));
    session.setActionHandler('previoustrack', () => setIndex(value => Math.max(0, value - 1)));
    session.setActionHandler('nexttrack', () => setIndex(value => Math.min(source.parts.length - 1, value + 1)));
    session.setActionHandler('stop', () => { request.current++; setPlaying(false); setSource(undefined); });
    return () => {
      for (const action of ['play', 'pause', 'previoustrack', 'nexttrack', 'stop'] as const) session.setActionHandler(action, null);
      session.metadata = null;
      session.playbackState = 'none';
    };
  }, [source]);

  useEffect(() => {
    if (source && 'mediaSession' in navigator) navigator.mediaSession.playbackState = playing ? 'playing' : 'paused';
  }, [source, playing]);
  const chapter = source?.chapters?.findLastIndex(start => start <= index) ?? -1;
  return <ListeningContext.Provider value={start}>
    <ListeningSettingsContext.Provider value={{ preferences, setPreferences: updatePreferences }}>{children}</ListeningSettingsContext.Provider>
    <audio ref={audio} src={source && clip ? clip : undefined} onEnded={() => {
      if (!source) return;
      if (index + 1 < source.parts.length) setIndex(index + 1);
      else { setPlaying(false); setIndex(0); }
    }} />
    {source && <aside className="listening-player" aria-label={tr('听书播放器', 'Narration player', '読み上げプレーヤー')}>
      <strong>{source.title}</strong>
      <div className="reader-controls">
        {source.chapters && <button disabled={chapter <= 0} onClick={() => setIndex(source.chapters![chapter - 1])}>{tr('上一章', 'Previous chapter', '前の章')}</button>}
        <button disabled={index <= 0} onClick={() => setIndex(index - 1)}>{tr('上一段', 'Previous passage', '前の段落')}</button>
        <button onClick={() => { setError(''); setPlaying(!playing); }}>{playing ? tr('暂停', 'Pause', '一時停止') : tr('继续', 'Resume', '再開')}</button>
        <button disabled={index + 1 >= source.parts.length} onClick={() => setIndex(index + 1)}>{tr('下一段', 'Next passage', '次の段落')}</button>
        {source.chapters && <button disabled={chapter + 1 >= source.chapters.length} onClick={() => setIndex(source.chapters![chapter + 1])}>{tr('下一章', 'Next chapter', '次の章')}</button>}
        <label>{tr('倍速', 'Speed', '再生速度')} <select value={rate} onChange={event => setRate(Number(event.target.value))}>
          {speedOptions.map(value => <option key={value} value={value}>{value}×</option>)}
        </select></label>
        <label>{tr('播放位置', 'Playback position', '再生位置')} <input type="range" min={0} max={source.parts.length - 1} value={index}
          onChange={event => setIndex(Number(event.target.value))} /></label>
        <output>{index + 1} / {source.parts.length}</output>
        <button onClick={() => { request.current++; setPlaying(false); setSource(undefined); }}>{tr('停止', 'Stop', tr('停止', 'Stop', '停止'))}</button>
      </div>
      {error && <p role="alert">{error}</p>}
    </aside>}
  </ListeningContext.Provider>;
}
