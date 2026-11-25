import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { access, mkdir, mkdtemp, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { promisify } from 'node:util';

const run = promisify(execFile);
const tar = join(process.env.SystemRoot ?? 'C:/Windows', 'System32', 'tar.exe');
const assets = [
  ['engine.tgz', 188408,
    'a5473360c303017772f514d149b4ce8e934e0140fadc8c5b61a81d9bba26598a'],
  ['dictionary.deb', 16063836,
    '1a03272cd580283029ca1e1af6c57c4fcee97a5a932059fab318547564e3333d'],
  ['mei_normal.htsvoice', 862503,
    'f3be49a6838904a6c218790b64e07c3e83c1886e995dca284b413caab19184de'],
  ['voice-license.txt', 2870,
    'af8a05b6ce28e513658fdf0480f36dbf49cbe878d2571d97dd017e8052c71391']
];
const dictionary = 'var/lib/mecab/dic/open-jtalk/naist-jdic';
const options = { windowsHide: true, timeout: 30000 };

async function readAsset(directory, [name, size, digest]) {
  const bytes = await readFile(join(directory, name));
  if (bytes.length !== size || createHash('sha256').update(bytes).digest('hex') !== digest) {
    throw new Error('Voice checksum mismatch: ' + name);
  }
  return bytes;
}

export function createJapaneseSpeech(cache, assetDirectory) {
  const installed = join(cache, 'open-jtalk-mei-v1');
  let installation;

  async function install() {
    if (await available()) return;
    await mkdir(cache, { recursive: true });
    const stage = await mkdtemp(join(cache, 'install-'));
    try {
      for (const asset of assets) await writeFile(join(stage, asset[0]), await readAsset(assetDirectory, asset));
      await run(tar, ['-xzf', 'engine.tgz'], { ...options, cwd: stage });
      await run(tar, ['-xf', 'dictionary.deb', 'data.tar.xz'], { ...options, cwd: stage });
      await run(tar, ['-xf', 'data.tar.xz'], { ...options, cwd: stage });
      for (const archive of ['engine.tgz', 'dictionary.deb', 'data.tar.xz']) await rm(join(stage, archive));
      // This Emscripten build loads its local WASM through Node when fetch is absent.
      await writeFile(join(stage, 'local-wasm.cjs'), 'globalThis.fetch = undefined;\n');
      await rename(stage, installed);
    } finally {
      await rm(stage, { recursive: true, force: true });
    }
  }

  async function available() {
    return access(join(installed, 'local-wasm.cjs')).then(() => true, error => {
      if (error.code === 'ENOENT') return false;
      throw error;
    });
  }

  return {
    install() {
      installation ??= install().finally(() => { installation = undefined; });
      return installation;
    },
    async generate(text, signal) {
      signal.throwIfAborted();
      if (!await available()) return null;
      const temporary = await mkdtemp(join(cache, 'speech-'));
      try {
        const input = join(temporary, 'input.txt');
        const output = join(temporary, 'speech.wav');
        await writeFile(input, text.replace(/[\r\n]+/g, ' ') + '\n', { signal });
        await run(process.execPath, [
          '--require', './local-wasm.cjs', 'package/js/open_jtalk.js',
          '-x', dictionary, '-m', 'mei_normal.htsvoice', '-ow', output, input
        ], { ...options, cwd: installed, signal, env: { ...process.env, ELECTRON_RUN_AS_NODE: '1' } });
        const audio = await readFile(output, { signal });
        if (audio.length <= 44 || audio.toString('ascii', 0, 4) !== 'RIFF' ||
            audio.toString('ascii', 8, 12) !== 'WAVE') throw new Error('Invalid narration audio');
        return new Uint8Array(audio);
      } finally {
        await rm(temporary, { recursive: true, force: true });
      }
    }
  };
}
