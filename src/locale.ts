import { createContext, useContext } from 'react';

export type Locale = 'zh-CN' | 'en' | 'ja';
export type LocalePreference = Locale | 'system';

export const LocaleContext = createContext<Locale>('zh-CN');

export function loadLocalePreference(): LocalePreference {
  const saved = localStorage.getItem('interface-language');
  return saved === 'zh-CN' || saved === 'en' || saved === 'ja' ? saved : 'system';
}

export function resolveLocale(preference: LocalePreference): Locale {
  if (preference !== 'system') return preference;
  const language = navigator.language.split('-')[0].toLowerCase();
  return language === 'zh' ? 'zh-CN' : language === 'ja' ? 'ja' : 'en';
}

export function translator(locale: Locale) {
  return (zh: string, en: string, ja: string) => locale === 'zh-CN' ? zh : locale === 'ja' ? ja : en;
}

export function useTranslation() {
  return translator(useContext(LocaleContext));
}

export type TextLanguage = 'zh' | 'en' | 'ja';

export function detectTextLanguage(text: string): TextLanguage {
  return /\p{Script=Hiragana}|\p{Script=Katakana}/u.test(text) ? 'ja' :
    /\p{Script=Han}/u.test(text) ? 'zh' : 'en';
}

export function loadTranslationTarget(): TextLanguage | 'interface' {
  const saved = localStorage.getItem('translation-target');
  return saved === 'zh' || saved === 'en' || saved === 'ja' ? saved : 'interface';
}

type TranslateLabel = ReturnType<typeof translator>;
const operations: Record<string, [string, string]> = {
  "未安装对应语言的 Windows 本地语音。": ["Install the corresponding Windows voice to listen in this language.", "この言語の Windows 音声をインストールしてください。"],
  "无法生成本地语音。": ["Could not generate speech on this device.", "端末で音声を生成できませんでした。"],
  "缺少离线 OCR 语言包，请重新安装应用": ["An OCR language pack is missing. Reinstall SeaReader.", "OCR 言語パックがありません。SeaReader を再インストールしてください。"],
  "无效的图片或识别语言": ["The image or OCR language is invalid.", "画像または認識言語が無効です。"],
  "请等待当前识别完成": ["Wait for recognition to finish.", "認識が終わるまでお待ちください。"],
  "已取消识别": ["Recognition was cancelled.", "認識をキャンセルしました。"],
  "无效的页码或识别内容": ["The page number or recognized text is invalid.", "ページ番号または認識テキストが無効です。"],
  "无法生成页面预览": ["Could not preview this page.", "ページをプレビューできませんでした。"],
  "请等待当前翻译任务结束": ["Wait for the current translation task to finish.", "翻訳処理が終わるまでお待ちください。"],
  "无效的翻译请求": ["The translation request is invalid.", "翻訳リクエストが無効です。"],
  "该语言方向的离线模型尚未安装": ["Install the offline model for these languages.", "この言語の組み合わせに対応するオフラインモデルをインストールしてください。"],
  "无法安装翻译模型，请检查网络和磁盘空间": ["Could not install the model. Check your connection and available disk space.", "モデルをインストールできませんでした。接続と空き容量を確認してください。"],
  "离线翻译失败": ["Offline translation failed.", "オフライン翻訳に失敗しました。"],
  "翻译任务已结束": ["The translation task ended.", "翻訳処理が終了しました。"],
  "仅支持 EPUB、TXT 和 PDF": ["Supported formats are EPUB, TXT and PDF.", "EPUB、TXT、PDF に対応しています。"],
  "文件为空": ["The file is empty.", "ファイルが空です。"],
  "PDF 文件格式无效": ["The PDF file is invalid.", "PDF ファイルが無効です。"],
  "EPUB 文件格式无效": ["The EPUB file is invalid.", "EPUB ファイルが無効です。"],
  "书籍不存在": ["This book is missing from the library.", "この本はライブラリにありません。"],
  "当前位置尚未保存": ["The current position has not been saved yet.", "現在の位置はまだ保存されていません。"],
  "无效的下载地址": ["The download URL is invalid.", "ダウンロード URL が無効です。"],
  "请输入 HTTP、HTTPS 或 FTP 文件地址": ["Enter an HTTP, HTTPS or FTP file URL.", "HTTP、HTTPS、FTP のいずれかのファイル URL を入力してください。"],
  "下载文件名无效": ["The download filename is invalid.", "ダウンロードファイル名が無効です。"],
  "下载未完成": ["The download is not complete.", "ダウンロードが完了していません。"],
  "下载已取消": ["The download was cancelled.", "ダウンロードをキャンセルしました。"],
  "下载失败，可在下载页重试": ["Download failed. Retry from Downloads.", "ダウンロードに失敗しました。ダウンロード画面で再試行してください。"],
  "仅支持预览 EPUB、TXT 和 PDF": ["Preview supports EPUB, TXT and PDF.", "プレビューは EPUB、TXT、PDF に対応しています。"],
  "下载文件为空": ["The downloaded file is empty.", "ダウンロードしたファイルが空です。"],
  "请等待下载完成": ["Wait for the download to finish.", "ダウンロードが終わるまでお待ちください。"],
  "请先取消下载": ["Cancel this download first.", "先にダウンロードをキャンセルしてください。"],
  "下载已中断，可以重试": ["The download was interrupted. You can retry it.", "ダウンロードが中断されました。再試行できます。"],
  "当前没有可用的 Wi-Fi 下载连接。连接 Wi-Fi 后可以重试。": ["Connect to Wi-Fi and retry the download.", "Wi-Fi に接続してから再試行してください。"],
  "下载失败，请检查地址和网络。": ["Download failed. Check the URL and your connection.", "ダウンロードに失敗しました。URL と接続を確認してください。"],
  "无效的书源地址": ["The feed URL is invalid.", "フィード URL が無効です。"],
  "请输入 HTTP 或 HTTPS 地址": ["Enter an HTTP or HTTPS URL.", "HTTP または HTTPS の URL を入力してください。"],
  "书源响应过大": ["The feed response is too large.", "フィードの応答が大きすぎます。"],
  "书源重定向次数过多": ["The feed redirected too many times.", "フィードのリダイレクト回数が多すぎます。"],
  "书源请求失败": ["The feed request failed.", "フィードの取得に失敗しました。"],
  "RSS 书源缺少频道。": ["The RSS feed has no channel.", "RSS フィードにチャンネルがありません。"],
  "无法读取书源，请提供 RSS 或 OPDS 地址。": ["Enter a valid RSS or OPDS feed URL.", "有効な RSS または OPDS フィードの URL を入力してください。"],
};

export function formatOperationError(reason: unknown, tr: TranslateLabel): string {
  const message = (reason instanceof Error ? reason.message : String(reason))
    .replace(/^Error invoking remote method '[^']+':\s*(?:Error:\s*)?/, '');
  const separator = message.lastIndexOf(' · ');
  const prefix = separator < 0 ? '' : message.slice(0, separator + 3);
  const content = separator < 0 ? message : message.slice(separator + 3);
  const suffix = content.match(/ \(\d+\)$/)?.[0] ?? '';
  const key = suffix ? content.slice(0, -suffix.length) : content;
  const labels = Object.hasOwn(operations, key) ? operations[key] : undefined;
  return prefix + (labels ? tr(key, labels[0], labels[1]) + suffix : content);
}
