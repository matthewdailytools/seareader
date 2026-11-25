import { formatOperationError, detectTextLanguage, loadTranslationTarget, LocaleContext, useTranslation, type TextLanguage } from './locale';
import { useContext, useEffect, useRef, useState } from 'react';

const codes = ['zh', 'en', 'ja'] as const;

export function TranslationPanel({ seed }: { seed?: string }) {
  const tr = useTranslation();
  const [text, setText] = useState('');
  const locale = useContext(LocaleContext);
  const [source, setSource] = useState<TextLanguage | 'auto'>('auto');
  const sourceLanguage = source === 'auto' ? detectTextLanguage(text) : source;
  const languageNames = tr('简体中文|英语|日语', 'Simplified Chinese|English|Japanese', '簡体字中国語|英語|日本語').split('|');
  const [target, setTarget] = useState<TextLanguage>(() => {
    const preference = loadTranslationTarget();
    return preference === 'interface' ? (locale === 'zh-CN' ? 'zh' : locale) : preference;
  });
  const [result, setResult] = useState('');
  const [status, setStatus] = useState('');
  const [busy, setBusy] = useState(false);
  const job = useRef<string | null>(null);

  useEffect(() => {
    if (!seed) return;
    setText(seed.slice(0, 6000));
    setResult('');
  }, [seed]);

  useEffect(() => () => {
    if (job.current) void window.seaReader.cancelTranslation(job.current).catch(() => {});
    job.current = null;
  }, []);

  async function run(install: boolean) {
    const id = crypto.randomUUID();
    job.current = id;
    setBusy(true);
    setStatus(install ? tr('正在安装此语言方向的模型…', 'Installing translation model…', '翻訳モデルをインストール中…') : tr('正在本机翻译…', 'Translating on this device…', '端末上で翻訳中…'));
    try {
      const output = await window.seaReader.translate(id, sourceLanguage, target, text, install);
      if (job.current !== id) return;
      if (!install) setResult(output);
      setStatus(install ? tr('模型已安装，可以离线翻译。', 'The model is ready for offline translation.', 'オフライン翻訳を利用できます。') : '');
    } catch (error) {
      if (job.current === id) setStatus(formatOperationError(error, tr));
    } finally {
      if (job.current === id) {
        job.current = null;
        setBusy(false);
      }
    }
  }

  return <div className="contents">
    <div className="reader-controls">
      <label>{tr('原文语言', 'Source language', '原文の言語')} <select value={source} disabled={busy} onChange={event => setSource(event.target.value as TextLanguage | 'auto')}>
        <option value="auto">{tr('自动识别', 'Detect automatically', '自動検出')}</option>
        {codes.map((code, index) => <option key={code} value={code}>{languageNames[index]}</option>)}
      </select></label>
      <label>{tr('目标语言', 'Target language', '翻訳先の言語')} <select value={target} disabled={busy} onChange={event => setTarget(event.target.value as TextLanguage)}>
        {codes.map((code, index) => <option key={code} value={code}>{languageNames[index]}</option>)}
      </select></label>
      <button disabled={busy || sourceLanguage === target || (source === 'auto' && !text.trim())} onClick={() => void run(true)}>{tr('安装离线模型', 'Install offline model', 'オフラインモデルをインストール')}</button>
      <button disabled={busy || sourceLanguage === target || !text.trim()} onClick={() => void run(false)}>{tr('翻译', 'Translate', '翻訳')}</button>
      {busy && <button onClick={() => {
        const id = job.current;
        if (id) void window.seaReader.cancelTranslation(id).catch(() => {});
        job.current = null;
        setBusy(false);
        setStatus(tr('已取消。', 'Cancelled.', 'キャンセルしました。'));
      }}>{tr('取消', 'Cancel', 'キャンセル')}</button>}
    </div>
    <p><label>{tr('原文', 'Original', '原文')} <textarea value={text} maxLength={6000} onChange={event => setText(event.target.value)} /></label></p>
    {result && <>
      <p><label>{tr('译文', 'Translation', '訳文')} <textarea readOnly value={result} /></label></p>
      <button onClick={() => void navigator.clipboard.writeText(result).then(
        () => setStatus(tr('已复制译文。', 'Translation copied.', '訳文をコピーしました。')), () => setStatus(tr('无法复制译文。', 'Could not copy the translation.', '訳文をコピーできませんでした。'))
      )}>{tr('复制译文', 'Copy translation', '訳文をコピー')}</button>
      <button onClick={() => setResult('')}>{tr('清空译文', 'Clear translation', '訳文を消去')}</button>
    </>}
    {status && <p role="status">{status}</p>}
  </div>;
}
