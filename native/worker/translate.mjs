import { text } from 'node:stream/consumers';
import { isAbsolute } from 'node:path';
import { randomUUID } from 'node:crypto';
import { registerTranslation } from '../../desktop/translation.mjs';

try {
  const request = JSON.parse(await text(process.stdin));
  if (typeof request.cacheDirectory !== 'string' || !isAbsolute(request.cacheDirectory)) {
    throw new Error('无效的离线模型目录。');
  }
  const handlers = new Map();
  registerTranslation((name, handler) => handlers.set(name, handler), request.cacheDirectory);
  const result = await handlers.get('translation:run')(randomUUID(), request.source,
    request.target, request.text, request.install);
  process.stdout.write('SEAREADER_RESULT ' + JSON.stringify({ result }) + '\n');
} catch (error) {
  process.stdout.write('SEAREADER_RESULT ' + JSON.stringify({ error: error.message }) + '\n');
  process.exitCode = 1;
}
