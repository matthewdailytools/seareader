import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream } from 'node:fs';
import { mkdir, rename, rm, stat } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';

const files = [
  'config.json', 'generation_config.json', 'tokenizer.json', 'tokenizer_config.json',
  'onnx/encoder_model_quantized.onnx', 'onnx/decoder_model_merged_quantized.onnx'
];
const hashes = {
  'Xenova/m2m100_418M': [
    '4f466cf3049e59ef8bb5de9b67f4275fa8f7257a', '434f8e78dc0d584e14d179bd47be7e329df86cb0',
    'cd68f923c35fc0da05d4bb83125bdcbe6ff9211b', 'b177ccaad20b2538951a05fcaf7093bd2b0a92b1',
    '13a94e354a9140764eb81102d77d3ec6952d796e6f113c651eeb3c3443da0386', '007654bcabb6cea6fd3bde34ce933137b431330b3755781145d7b6906270b45a'
  ],
  'Xenova/opus-mt-zh-en': [
    'dcd3adfa622976cc4a51c68d115dc9d443d3cbd0', '11cf47bfd6c2e512bf7ab5d58d49ab109732647a',
    '545ab64f6d0ae9bbbb0c8899f04e1ae4f805fc87', '9bae5b64c3a664f32d2aac60cbde97fc9e78a5ef',
    '84d5e171b626bc8b6b220d022ac58696e9528c25deeacca62b5cbf4364547a99', 'c6b7f04ff1ba0fbd1bf6852599b4c0cad6fe512d57cd887f44ef36cf705424cb'
  ],
  'Xenova/opus-mt-en-zh': [
    '787a84798a695a75720fa01ad2881c8d193c45f2', '015c3e06e8eac3e32e2078ba3140f246e754ae7b',
    '7106ca5b95c748f039dd9adcc13c7542b997762d', '5d963ba4e41e1f0926f052dd3d04678d0819f027',
    'd3b7912bf6a9bd27e4c074c2df91d4ff3d5b4bc5f7f6c8d7cc9c805c98fbafee', '023be4f841f4c47cd65fffcbaa81c0d99d7f7e0138f7ba0e03fa220a4e688aff'
  ],
  'Xenova/opus-mt-ja-en': [
    'c64ee5662fb65d93fcca8837b957650a6e97e3fd', '3f32cba8c6a674550f8e5490a739f63fb129e5cb',
    '2183cfa1bb47d494426f4903a86c19097dfd4e0e', '253a5ecae27b3caca9aa63ea8c3763bc328bcc9f',
    '345262b16bcdda1468b0f3380c112b7ce79f731176b4b1d21f6edd5b2ae0d25c', 'b304d0014e4e1575437b6af95467b6cb54405d923732d8359113bd6dbbee93c0'
  ],
};

async function validFile(path, expected) {
  try {
    const { size } = await stat(path);
    const gitBlob = expected.length === 40;
    const hash = createHash(gitBlob ? 'sha1' : 'sha256');
    if (gitBlob) hash.update('blob ' + size + '\0');
    for await (const chunk of createReadStream(path)) hash.update(chunk);
    return hash.digest('hex') === expected;
  } catch {
    return false;
  }
}

export async function prepareModel(cacheDir, model, install) {
  const root = join(cacheDir, model.revision);
  const records = await Promise.allSettled(files.map(async (file, index) => {
    const target = join(root, model.name, file);
    const expected = hashes[model.name][index];
    if (await validFile(target, expected)) return;
    if (!install) throw new Error('Offline model file missing or corrupt in cache');
    await mkdir(dirname(target), { recursive: true });
    const temporary = target + '.part';
    try {
      let latestError;
      for (const host of ['https://huggingface.co', 'https://hf-mirror.com']) {
        try {
          const url = host + '/' + model.name + '/resolve/' + model.revision + '/' + file;
          const response = await fetch(url, { signal: AbortSignal.timeout(480000) });
          if (!response.ok || !response.body) {
            await response.body?.cancel();
            throw new Error('Model download HTTP ' + response.status);
          }
          await pipeline(Readable.fromWeb(response.body), createWriteStream(temporary));
          if (!await validFile(temporary, expected)) throw new Error('Model file checksum mismatch');
          await rename(temporary, target);
          return;
        } catch (error) {
          latestError = error;
        }
      }
      throw latestError;
    } finally {
      await rm(temporary, { force: true });
    }
  }));
  const failure = records.find(record => record.status === 'rejected');
  if (failure) throw failure.reason;
  return root;
}
