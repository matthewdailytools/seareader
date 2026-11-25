import { execFile } from 'node:child_process';
import { join } from 'node:path';
import { createJapaneseSpeech } from './japanese-speech.mjs';

const script = `
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Speech
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
$speech = New-Object System.Speech.Synthesis.SpeechSynthesizer
$stream = New-Object System.IO.MemoryStream
try {
  $voice = $speech.GetInstalledVoices() | Where-Object {
    $_.Enabled -and $_.VoiceInfo.Culture.TwoLetterISOLanguageName -eq $request.language
  } | Select-Object -First 1
  if (!$voice) { exit 4 }
  $speech.SelectVoice($voice.VoiceInfo.Name)
  $speech.SetOutputToWaveStream($stream)
  $speech.Speak([string]$request.text)
  $speech.SetOutputToNull()
  [Console]::Write([Convert]::ToBase64String($stream.ToArray()))
} finally {
  $speech.Dispose()
  $stream.Dispose()
}
`;
const command = Buffer.from(script, 'utf16le').toString('base64');
const executable = join(process.env.SystemRoot ?? 'C:/Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
const languages = new Set(['zh', 'en', 'ja']);

function generateWindowsSpeech(text, language, signal) {
  return new Promise((resolve, reject) => {
    const child = execFile(executable, ['-NoProfile', '-NonInteractive', '-EncodedCommand', command], {
      windowsHide: true, timeout: 30000, maxBuffer: 8 * 1024 * 1024, signal
    }, (error, stdout) => {
      if (error?.code === 4) resolve(null);
      else if (error || !stdout) reject(new Error('无法生成本地语音。'));
      else resolve(new Uint8Array(Buffer.from(stdout, 'base64')));
    });
    child.stdin.on('error', () => {});
    child.stdin.end(JSON.stringify({ text, language }), 'utf8');
  });
}

export function registerSpeech(register, cache, assetDirectory) {
  const japanese = createJapaneseSpeech(cache, assetDirectory);
  let active;
  register('speech:install-japanese', async () => {
    try {
      await japanese.install();
    } catch {
      throw new Error('无法安装日语离线语音，请确认磁盘空间充足且安装包完整。');
    }
  });
  register('speech:generate', async (id, text, language) => {
    if (typeof id !== 'string' || !/^[a-f0-9-]{36}$/.test(id) ||
        typeof text !== 'string' || !text.trim() || text.length > 800 || !languages.has(language)) {
      throw new Error('无效的朗读请求');
    }
    active?.controller.abort();
    const controller = new AbortController();
    active = { id, controller };
    try {
      const local = language === 'ja' ? await japanese.generate(text, controller.signal) : null;
      return local ?? await generateWindowsSpeech(text, language, controller.signal);
    } catch {
      throw new Error('无法生成本地语音。');
    } finally {
      if (active?.controller === controller) active = undefined;
    }
  });
  register('speech:cancel', id => {
    if (active?.id === id) active.controller.abort();
  });
}
