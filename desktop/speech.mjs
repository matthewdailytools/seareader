import { execFile } from 'node:child_process';
import { join } from 'node:path';

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

export function registerSpeech(register) {
  let active;
  register('speech:generate', (id, text, language) => {
    if (typeof id !== 'string' || !/^[a-f0-9-]{36}$/.test(id) ||
        typeof text !== 'string' || !text.trim() || text.length > 800 || !languages.has(language)) {
      throw new Error('无效的朗读请求');
    }
    active?.child.kill();
    return new Promise((resolve, reject) => {
      const child = execFile(executable, ['-NoProfile', '-NonInteractive', '-EncodedCommand', command], {
        windowsHide: true, timeout: 30000, maxBuffer: 8 * 1024 * 1024
      }, (error, stdout) => {
        if (active?.child === child) active = undefined;
        if (error || !stdout) {
          reject(new Error(error?.code === 4 ? '未安装对应语言的 Windows 本地语音。' : '无法生成本地语音。'));
          return;
        }
        resolve(new Uint8Array(Buffer.from(stdout, 'base64')));
      });
      active = { id, child };
      child.stdin.on('error', () => {});
      child.stdin.end(JSON.stringify({ text, language }), 'utf8');
    });
  });
  register('speech:cancel', id => {
    if (active?.id === id) active.child.kill();
  });
}
