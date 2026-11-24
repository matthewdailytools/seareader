import { tmpdir } from 'node:os';
import { access, copyFile, mkdtemp, rm, rmdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { join } from 'node:path';

const require = createRequire(import.meta.url);
const languages = ['eng', 'chi_sim', 'jpn'];

async function modelPath(code) {
  const packageName = '@tesseract.js-data/' + code;
  for (const base of require.resolve.paths(packageName) ?? []) {
    const candidate = join(base, packageName, '4.0.0_best_int');
    try {
      await access(join(candidate, code + '.traineddata.gz'));
      return candidate;
    } catch { /* Check the next module directory. */ }
  }
  throw new Error('缺少离线 OCR 语言包，请重新安装应用');
}

export function registerOcr(register, database, requireBook) {
  database.exec(`
    CREATE TABLE IF NOT EXISTS recognized_pages (
      book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
      page INTEGER NOT NULL,
      text TEXT NOT NULL,
      PRIMARY KEY (book_id, page)
    )
  `);
  let active;

  register('ocr:recognize', async (jobId, image, language) => {
    if (typeof jobId !== 'string' || !/^[a-f0-9-]{36}$/.test(jobId) || !(image instanceof Uint8Array) || image.byteLength > 25 * 1024 * 1024 ||
        image.byteLength === 0 || !(language === 'auto' || languages.includes(language))) throw new Error('无效的图片或识别语言');
    if (active) throw new Error('请等待当前识别完成');
    const job = { id: jobId, controller: new AbortController(), worker: undefined };
    active = job;
    const signal = job.controller.signal;
    let staging;
    try {
      const { createWorker } = await import('tesseract.js');
      signal.throwIfAborted();
      const codes = language === 'auto' ? languages : [language];
      const paths = await Promise.all(codes.map(modelPath));
      let langPath = paths[0];
      if (codes.length > 1) {
        staging = await mkdtemp(join(tmpdir(), 'seareader-ocr-'));
        langPath = staging;
        const copies = await Promise.allSettled(codes.map((code, index) => {
          const name = code + '.traineddata.gz';
          return copyFile(join(paths[index], name), join(langPath, name));
        }));
        const failed = copies.find(copy => copy.status === 'rejected');
        if (failed) throw failed.reason;
      }
      signal.throwIfAborted();
      job.worker = await createWorker(codes.join('+'), 1, { langPath, cacheMethod: 'none' });
      signal.throwIfAborted();
      const cancelled = new Promise((_, reject) => {
        signal.addEventListener('abort', () => reject(signal.reason), { once: true });
      });
      const result = await Promise.race([job.worker.recognize(Buffer.from(image)), cancelled]);
      return result.data.text.trim();
    } finally {
      try { await job.worker?.terminate(); }
      finally {
        if (staging) {
          await Promise.allSettled(languages.map(code => rm(join(staging, code + '.traineddata.gz'), { force: true })));
          await rmdir(staging).catch(() => {});
        }
        active = undefined;
      }
    }
  });

  register('ocr:cancel', jobId => {
    if (active?.id === jobId) active.controller.abort(new Error('已取消识别'));
  });

  register('ocr:list', (id) => {
    requireBook(id);
    return database.prepare('SELECT page, text FROM recognized_pages WHERE book_id = ? ORDER BY page').all(id);
  });
  register('ocr:save', (id, page, text) => {
    requireBook(id);
    if (!Number.isSafeInteger(page) || page < 1 || typeof text !== 'string' || text.length > 250000) {
      throw new Error('无效的页码或识别内容');
    }
    if (!text.trim()) {
      database.prepare('DELETE FROM recognized_pages WHERE book_id = ? AND page = ?').run(id, page);
      return;
    }
    database.prepare(`
      INSERT INTO recognized_pages (book_id, page, text) VALUES (?, ?, ?)
      ON CONFLICT(book_id, page) DO UPDATE SET text = excluded.text
    `).run(id, page, text);
  });
}
