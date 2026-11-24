import { registerSpeech } from './speech.mjs';
import { registerTranslation } from './translation.mjs';
import { registerFeeds } from './feeds.mjs';
import { registerDownloads } from './downloads.mjs';
import { registerOcr } from './ocr.mjs';
import { app, BrowserWindow, dialog, ipcMain, shell } from 'electron';
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { basename, extname, isAbsolute, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { DatabaseSync } from 'node:sqlite';

const renderer = fileURLToPath(new URL('../dist/index.html', import.meta.url));
const rendererUrl = pathToFileURL(renderer).href;
const extensions = ['epub', 'txt', 'pdf'];

function bookArguments(argv) {
  return argv.filter(path => isAbsolute(path) && extensions.includes(extname(path).slice(1).toLowerCase()));
}

const work = app.requestSingleInstanceLock();
const pending = bookArguments(process.argv.slice(app.isPackaged ? 1 : 2));
let deliver;
app.on('second-instance', (_event, argv) => {
  const paths = bookArguments(argv.slice(app.isPackaged ? 1 : 2));
  if (deliver) deliver(paths);
  else pending.push(...paths);
});

if (!work) app.quit();
else app.whenReady().then(async () => {
  const bookFolder = join(app.getPath('userData'), 'books');
  await mkdir(bookFolder, { recursive: true });
  const database = new DatabaseSync(join(app.getPath('userData'), 'library.sqlite'));
  database.exec(`
    PRAGMA journal_mode = WAL;
    PRAGMA foreign_keys = ON;
    CREATE TABLE IF NOT EXISTS books (
      id TEXT PRIMARY KEY, title TEXT NOT NULL, format TEXT NOT NULL,
      imported_at INTEGER NOT NULL
    );
  `);

  database.exec(`
    CREATE TABLE IF NOT EXISTS application_preferences (
      key TEXT PRIMARY KEY, value TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS reading_positions (
      book_id TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE,
      position TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS listening_positions (
      source TEXT PRIMARY KEY,
      book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
      part INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS bookmarks (
      id INTEGER PRIMARY KEY,
      book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
      position TEXT NOT NULL,
      created_at INTEGER NOT NULL,
      UNIQUE(book_id, position)
    );
    CREATE TABLE IF NOT EXISTS annotations (
      id INTEGER PRIMARY KEY,
      book_id TEXT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
      position TEXT NOT NULL,
      quotation TEXT NOT NULL,
      note TEXT NOT NULL,
      updated_at INTEGER NOT NULL,
      UNIQUE(book_id, position)
    );
    CREATE TABLE IF NOT EXISTS book_activity (
      book_id TEXT PRIMARY KEY REFERENCES books(id) ON DELETE CASCADE,
      opened_at INTEGER NOT NULL
    );
  `);

  if (database.prepare('PRAGMA user_version').get().user_version < 7) {
    database.exec('PRAGMA user_version = 7');
  }

  function register(channel, operation) {
    ipcMain.handle(channel, (event, ...args) => {
      if (event.senderFrame?.url !== rendererUrl) throw new Error('Invalid caller');
      return operation(...args);
    });
  }

  const listBooks = () => database.prepare(
    `SELECT books.id, title, format, book_activity.opened_at AS openedAt
       FROM books LEFT JOIN book_activity ON books.id = book_activity.book_id
       ORDER BY book_activity.opened_at DESC, imported_at DESC, books.id`
  ).all();

  async function importBook(path) {
    const format = extname(path).slice(1).toLowerCase();
    if (!extensions.includes(format)) throw new Error('仅支持 EPUB、TXT 和 PDF');
    const bytes = await readFile(path);
    if (!bytes.length) throw new Error('文件为空');
    if (format === 'pdf' && !bytes.subarray(0, 1024).includes('%PDF-')) {
      throw new Error('PDF 文件格式无效');
    }
    if (format === 'epub' && bytes.subarray(0, 2).toString() !== 'PK') {
      throw new Error('EPUB 文件格式无效');
    }
    const id = createHash('sha256').update(bytes).digest('hex');
    if (database.prepare('SELECT id FROM books WHERE id = ?').get(id)) return id;
    const temporaryPath = join(bookFolder, id + '.' + randomUUID() + '.tmp');
    try {
      await writeFile(temporaryPath, bytes, { flag: 'wx' });
      await rename(temporaryPath, join(bookFolder, id));
    } finally {
      await rm(temporaryPath, { force: true });
    }
    database.prepare('INSERT OR IGNORE INTO books VALUES (?, ?, ?, ?)').run(
      id, basename(path, extname(path)), format, Date.now()
    );
    return id;
  }

  function requireBook(id) {
    if (typeof id !== 'string' || !/^[a-f0-9]{64}$/.test(id)) throw new Error('无效的书籍 ID');
    if (!database.prepare('SELECT id FROM books WHERE id = ?').get(id)) throw new Error('书籍不存在');
  }

  registerOcr(register, database, requireBook);
  registerSpeech(register);
  registerTranslation(register, join(app.getPath('userData'), 'translation-models'));
  registerFeeds(register, database, url => shell.openExternal(url));
  registerDownloads(register, database, join(app.getPath('userData'), 'downloads'), importBook);
  const annotationFields = 'id, book_id AS bookId, position, quotation, note, updated_at AS updatedAt';
  register('annotations:list', (id) => {
    requireBook(id);
    return database.prepare(`SELECT ${annotationFields} FROM annotations WHERE book_id = ? ORDER BY updated_at DESC, id DESC`).all(id);
  });
  register('annotations:save', (id, position, quotation, note) => {
    requireBook(id);
    if (typeof position !== 'string' || !position || position.length > 4096 ||
        typeof quotation !== 'string' || !quotation.trim() || quotation.length > 12000 ||
        typeof note !== 'string' || note.length > 12000) throw new Error('无效的标注内容');
    database.prepare(`
      INSERT INTO annotations (book_id, position, quotation, note, updated_at) VALUES (?, ?, ?, ?, ?)
      ON CONFLICT(book_id, position) DO UPDATE SET quotation = excluded.quotation,
        note = excluded.note, updated_at = excluded.updated_at
    `).run(id, position, quotation, note, Date.now());
  });
  register('annotations:remove', (id, annotationId) => {
    requireBook(id);
    if (!Number.isSafeInteger(annotationId)) throw new Error('无效的标注');
    database.prepare('DELETE FROM annotations WHERE book_id = ? AND id = ?').run(id, annotationId);
  });
  register('bookmarks:list', (id) => {
    requireBook(id);
    return database.prepare(
      'SELECT id, position, created_at AS createdAt FROM bookmarks WHERE book_id = ? ORDER BY created_at DESC, id DESC'
    ).all(id);
  });
  register('bookmarks:add', (id) => {
    requireBook(id);
    const current = database.prepare('SELECT position FROM reading_positions WHERE book_id = ?').get(id);
    if (!current) throw new Error('当前位置尚未保存');
    database.prepare('INSERT OR IGNORE INTO bookmarks (book_id, position, created_at) VALUES (?, ?, ?)').run(
      id, current.position, Date.now()
    );
  });
  register('bookmarks:remove', (id, bookmarkId) => {
    requireBook(id);
    if (!Number.isSafeInteger(bookmarkId)) throw new Error('无效的书签');
    database.prepare('DELETE FROM bookmarks WHERE book_id = ? AND id = ?').run(id, bookmarkId);
  });
  function listeningBook(source) {
    const match = typeof source === 'string' &&
      /^(?:epub|pdf|txt):([a-f0-9]{64})(?::[a-z0-9_-]{1,32})?$/.exec(source);
    if (!match) throw new Error('无效的听书来源');
    requireBook(match[1]);
    return match[1];
  }

  register('listening:get', source => {
    listeningBook(source);
    return database.prepare('SELECT part FROM listening_positions WHERE source = ?').get(source)?.part ?? null;
  });
  register('listening:save', (source, part) => {
    const id = listeningBook(source);
    if (!Number.isSafeInteger(part) || part < 0) throw new Error('无效的听书位置');
    database.prepare(`
      INSERT INTO listening_positions (source, book_id, part) VALUES (?, ?, ?)
      ON CONFLICT(source) DO UPDATE SET part = excluded.part
    `).run(source, id, part);
  });

  register('positions:get', (id) => {
    requireBook(id);
    return database.prepare('SELECT position FROM reading_positions WHERE book_id = ?').get(id)?.position ?? null;
  });
  register('positions:save', (id, position) => {
    requireBook(id);
    if (typeof position !== 'string' || !position.length || position.length > 4096) {
      throw new Error('无效的阅读位置');
    }
    database.prepare(`
      INSERT INTO reading_positions (book_id, position) VALUES (?, ?)
      ON CONFLICT(book_id) DO UPDATE SET position = excluded.position
    `).run(id, position);
  });
  const preferenceKeys = new Set([
    'reading-appearance', 'listening-preferences', 'interface-language', 'library-sort',
    'resume-on-start', 'open-after-import', 'translation-target', 'page-turning',
    'epub-reading-mode', 'pdf-reading-mode', 'txt-reading-mode'
  ]);
  register('preferences:list', () => database.prepare('SELECT key, value FROM application_preferences').all());
  register('preferences:save', (key, value) => {
    if (!preferenceKeys.has(key) || (value !== null && (typeof value !== 'string' || value.length > 4096))) {
      throw new Error('无效的设置数据');
    }
    if (value === null) {
      database.prepare('DELETE FROM application_preferences WHERE key = ?').run(key);
      return;
    }
    database.prepare(`
      INSERT INTO application_preferences (key, value) VALUES (?, ?)
      ON CONFLICT(key) DO UPDATE SET value = excluded.value
    `).run(key, value);
  });

  register('data:export', async (preferences) => {
    if (typeof preferences !== 'string' || preferences.length > 16384) throw new Error('无效的设置数据');
    const exportData = {
      version: 1,
      exportedAt: new Date().toISOString(),
      preferences: JSON.parse(preferences),
      books: listBooks(),
      readingPositions: database.prepare('SELECT book_id AS bookId, position FROM reading_positions').all(),
      bookmarks: database.prepare('SELECT id, book_id AS bookId, position, created_at AS createdAt FROM bookmarks').all(),
      listeningPositions: database.prepare('SELECT source, book_id AS bookId, part FROM listening_positions').all(),
      recognizedPages: database.prepare('SELECT book_id AS bookId, page, text FROM recognized_pages').all(),
      annotations: database.prepare(`SELECT ${annotationFields} FROM annotations`).all()
    };
    const result = await dialog.showSaveDialog({
      defaultPath: 'SeaReader-data.json',
      filters: [{ name: 'JSON', extensions: ['json'] }]
    });
    if (result.canceled || !result.filePath) return false;
    await writeFile(result.filePath, JSON.stringify(exportData, null, 2), 'utf8');
    return true;
  });
  register('books:list', listBooks);
  register('books:choose', async () => {
    const result = await dialog.showOpenDialog({
      properties: ['openFile', 'multiSelections'],
      filters: [{ name: 'EPUB / TXT / PDF', extensions }]
    });
    return importPaths(result.filePaths);
  });

  async function importPaths(paths) {
    if (!Array.isArray(paths) || paths.some(path => typeof path !== 'string' || !isAbsolute(path))) {
      throw new Error('无效的文件路径');
    }
    const errors = [];
    const openedIds = [];
    for (const path of paths) {
      try {
        openedIds.push(await importBook(path));
      } catch (error) {
        errors.push(basename(path) + ' · ' + error.message);
      }
    }
    return { books: listBooks(), errors, openedId: openedIds.at(-1), importedCount: openedIds.length };
  }
  register('books:import', importPaths);
  register('books:assets', async id => {
    requireBook(id);
    return new Uint8Array(await readFile(join(bookFolder, id)));
  });
  register('books:read', async (id) => {
    requireBook(id);
    const bytes = await readFile(join(bookFolder, id));
    database.prepare(`
      INSERT INTO book_activity (book_id, opened_at) VALUES (?, ?)
      ON CONFLICT(book_id) DO UPDATE SET opened_at = excluded.opened_at
    `).run(id, Date.now());
    return new Uint8Array(bytes);
  });

  const window = new BrowserWindow({
    width: 1200, height: 800, minWidth: 420, minHeight: 480,
    webPreferences: {
      preload: fileURLToPath(new URL('./preload.cjs', import.meta.url)),
      sandbox: true, contextIsolation: true, nodeIntegration: false
    }
  });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', (event) => event.preventDefault());
  let queue = Promise.resolve();
  let startupRequested = false;
  register('books:startup', () => {
    startupRequested = true;
    const paths = pending.splice(0);
    const startup = queue.then(() => importPaths(paths));
    queue = startup.then(() => {}, () => {});
    return startup;
  });
  await window.loadFile(renderer);
  deliver = (paths) => {
    if (window.isDestroyed()) return;
    if (window.isMinimized()) window.restore();
    window.focus();
    if (!paths.length) return;
    if (!startupRequested) {
      pending.push(...paths);
      return;
    }
    queue = queue.then(async () => {
      const result = await importPaths(paths);
      if (!window.isDestroyed()) window.webContents.send('books:opened', result);
    }).catch(error => {
      if (!window.isDestroyed()) dialog.showErrorBox('SeaReader', String(error));
    });
  };
  if (startupRequested && pending.length) deliver(pending.splice(0));
}).catch((error) => {
  dialog.showErrorBox('SeaReader', String(error));
  app.exit(1);
});

app.on('window-all-closed', () => app.quit());
