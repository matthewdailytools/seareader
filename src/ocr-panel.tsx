import { formatOperationError, useTranslation } from './locale';
import { TranslationPanel } from './translation-panel';
import { speechParts, useListening } from './listening';
import { useEffect, useRef, useState } from 'react';

export function OcrPanel({ capture, saved, onSave, selectedText }: {
  capture?: () => Promise<Blob>;
  saved?: string;
  selectedText?: string;
  onSave?: (text: string) => Promise<void>;
}) {
  const tr = useTranslation();
  const startListening = useListening();
  const [image, setImage] = useState<Blob>();
  const [preview, setPreview] = useState('');
  const [language, setLanguage] = useState('auto');
  const [text, setText] = useState(saved ?? '');
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState('');
  const [translationSeed, setTranslationSeed] = useState<{ text: string; revision: number }>();
  const job = useRef<string | null>(null);
  const [recognizing, setRecognizing] = useState(false);

  useEffect(() => () => {
    if (job.current) void window.seaReader.cancelRecognition(job.current).catch(() => {});
    job.current = null;
  }, []);

  async function recognize() {
    if (!image) return;
    const id = crypto.randomUUID();
    job.current = id;
    setRecognizing(true);
    setStatus('');
    try {
      const bytes = new Uint8Array(await image.arrayBuffer());
      if (job.current !== id) return;
      const result = await window.seaReader.recognizeImage(id, bytes, language);
      if (job.current !== id) return;
      setText(result);
      setStatus(result ? tr('识别完成。', 'Recognition complete.', '認識が完了しました。') : tr('未识别到文字。', 'No text found.', '文字が見つかりませんでした。'));
    } catch (error) {
      if (job.current === id) setStatus(formatOperationError(error, tr));
    } finally {
      if (job.current === id) {
        job.current = null;
        setRecognizing(false);
      }
    }
  }

  function cancel() {
    const id = job.current;
    if (!id) return;
    job.current = null;
    setRecognizing(false);
    setStatus(tr('已取消识别。', 'Recognition cancelled.', '認識をキャンセルしました。'));
    void window.seaReader.cancelRecognition(id).catch(() => {});
  }

  useEffect(() => {
    if (!image) return;
    const url = URL.createObjectURL(image);
    setPreview(url);
    return () => URL.revokeObjectURL(url);
  }, [image]);
  useEffect(() => setText(saved ?? ''), [saved]);

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setStatus('');
    try { await action(); }
    catch (error) { setStatus(formatOperationError(error, tr)); }
    finally { setBusy(false); }
  }

  return <>
  {selectedText && <button onClick={() => setTranslationSeed(current => ({
    text: selectedText, revision: (current?.revision ?? 0) + 1
  }))}>{tr('翻译选中文字', 'Translate selection', '選択した文字を翻訳')}</button>}
  <details className="contents">
    <summary>{tr('图片转文字', 'Image to text', '画像をテキスト化')}</summary>
    <div className="reader-controls">
      {capture && <button disabled={busy} onClick={() => void run(async () => setImage(await capture()))}>{tr('预览当前页', 'Preview current page', '現在のページをプレビュー')}</button>}
      <label>{tr('选择图片', 'Choose image', '画像を選択')} <input type="file" accept="image/png,image/jpeg,image/webp" disabled={busy}
        onChange={event => setImage(event.target.files?.[0])} /></label>
      <label>{tr('识别语言', 'Recognition language', '認識言語')} <select value={language} onChange={event => setLanguage(event.target.value)}>
        <option value="auto">{tr('自动（中英日）', 'Automatic (Chinese, English, Japanese)', '自動（中国語・英語・日本語）')}</option>
        <option value="chi_sim">{tr('简体中文', 'Simplified Chinese', '簡体字中国語')}</option>
        <option value="eng">{tr('英语', 'English', '英語')}</option>
        <option value="jpn">{tr('日语', 'Japanese', '日本語')}</option>
      </select></label>
      <button disabled={!image || busy || recognizing} onClick={() => void recognize()}>{tr('开始识别', 'Recognize', '認識を開始')}</button>
      {recognizing && <button onClick={cancel}>{tr('取消识别', 'Cancel recognition', '認識をキャンセル')}</button>}
    </div>
    {image && preview && <img className="ocr-preview" src={preview} alt={tr('待识别图片', 'Image to recognize', '認識対象の画像')} />}
    <p><label>{tr('识别结果', 'Recognized text', '認識結果')} <textarea value={text} maxLength={250000} onChange={event => setText(event.target.value)} /></label></p>
    <button disabled={!text || busy} onClick={() => void run(async () => {
      await navigator.clipboard.writeText(text);
      setStatus(tr('已复制。', 'Copied.', 'コピーしました。'));
    })}>{tr('复制', 'Copy', 'コピー')}</button>
    <button disabled={!text.trim() || busy || recognizing} onClick={() => startListening({
      id: '', title: tr('识别结果', 'Recognized text', '認識結果'), parts: speechParts(text)
    }, 0)}>{tr('朗读识别结果', 'Read recognized text aloud', '認識結果を読み上げる')}</button>
    <button disabled={!text.trim() || busy || recognizing} onClick={() =>
      setTranslationSeed(current => ({ text, revision: (current?.revision ?? 0) + 1 }))
    }>{tr('转入翻译', 'Translate result', '認識結果を翻訳')}</button>
    {onSave && <button disabled={busy} onClick={() => void run(async () => {
      await onSave(text);
      setStatus(tr('已保存本页文字。', 'Page text saved.', 'ページのテキストを保存しました。'));
    })}>{tr('保存到本页', 'Save to this page', 'このページに保存')}</button>}
    {(busy || recognizing || status) && <p role="status">{recognizing ? tr('正在识别…', 'Recognizing…', '認識中…') : busy ? tr('处理中…', 'Working…', '処理中…') : status}</p>}
  </details>
  <TranslationPanel key={translationSeed?.revision ?? 0} seed={translationSeed?.text} />
  </>;
}
