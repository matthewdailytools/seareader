import { isIPv4 } from 'node:net';
import { EventEmitter } from 'node:events';
import { execFile } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdir, readFile, rename, rm } from 'node:fs/promises';
import { basename, extname, join } from 'node:path';
import { promisify } from 'node:util';

const run = promisify(execFile);
const protocols = new Set(['http:', 'https:', 'ftp:']);

const windowsPowerShell = join(process.env.SystemRoot ?? 'C:/Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
const wifiQuery = `
$adapter = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.NdisPhysicalMedium -in 1,9 }
foreach ($item in $adapter) {
  $configuration = Get-NetIPConfiguration -InterfaceIndex $item.ifIndex
  if ($configuration.IPv4DefaultGateway -and $configuration.IPv4Address) {
    $configuration.IPv4Address[0].IPAddress
    break
  }
}
`;

async function connectedWifi(signal) {
  try {
    const { stdout } = await run(windowsPowerShell, ['-NoProfile', '-NonInteractive', '-Command', wifiQuery], {
      signal, windowsHide: true, timeout: 12000
    });
    const ip = stdout.trim();
    return isIPv4(ip) ? ip : null;
  } catch {
    return null;
  }
}

export function registerDownloads(register, database, folder, importBook, wifiAddress = connectedWifi) {
  database.exec(`
    CREATE TABLE IF NOT EXISTS downloads (
      id TEXT PRIMARY KEY, url TEXT NOT NULL, name TEXT NOT NULL,
      status TEXT NOT NULL, error TEXT, created_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS download_preferences (id INTEGER PRIMARY KEY CHECK (id = 1), network TEXT NOT NULL);
    INSERT OR IGNORE INTO download_preferences VALUES (1, 'any');
    UPDATE downloads SET status = 'failed', error = '下载已中断，可以重试'
      WHERE status IN ('queued', 'running');
  `);
  const list = database.prepare('SELECT id, url, name, status, error FROM downloads ORDER BY created_at DESC');
  const get = database.prepare('SELECT id, url, name, status FROM downloads WHERE id = ?');
  const events = new EventEmitter();
  const update = database.prepare('UPDATE downloads SET status = ?, error = ? WHERE id = ?');
  function setStatus(status, error, id) {
    update.run(status, error, id);
    events.emit(id, status);
  }
  const next = database.prepare("SELECT id, url, name FROM downloads WHERE status = 'queued' ORDER BY created_at, id LIMIT 1");
  const curl = join(process.env.SystemRoot ?? 'C:/Windows', 'System32', 'curl.exe');
  let active;
  const networkMode = () => database.prepare('SELECT network FROM download_preferences WHERE id = 1').get().network;

  register('downloads:network', networkMode);
  register('downloads:set-network', mode => {
    if (mode !== 'any' && mode !== 'wifi') throw new Error('无效的下载网络偏好');
    database.prepare('UPDATE download_preferences SET network = ? WHERE id = 1').run(mode);
  });

  async function drain() {
    if (active) return;
    const job = next.get();
    if (!job) return;
    const controller = new AbortController();
    active = { id: job.id, controller };
    const jobFolder = join(folder, job.id);
    const partial = join(jobFolder, 'partial');
    setStatus('running', null, job.id);
    try {
      await mkdir(jobFolder, { recursive: true });
      let networkArguments = [];
      if (networkMode() === 'wifi') {
        const ip = await wifiAddress(controller.signal);
        controller.signal.throwIfAborted();
        if (!ip) {
          setStatus('failed', '当前没有可用的 Wi-Fi 下载连接。连接 Wi-Fi 后可以重试。', job.id);
          return;
        }
        networkArguments = ['--interface', ip];
      }
      await run(curl, [
        ...networkArguments,
        '--disable', '--globoff', '--silent', '--show-error', '--fail', '--location',
        '--max-redirs', '5', '--connect-timeout', '15', '--max-time', '600',
        '--max-filesize', '536870912', '--proto', '=http,https,ftp',
        '--proto-redir', '=http,https,ftp', '--output', partial, '--', job.url
      ], { signal: controller.signal, windowsHide: true });
      controller.signal.throwIfAborted();
      await rename(partial, join(jobFolder, job.name));
      controller.signal.throwIfAborted();
      setStatus('completed', null, job.id);
    } catch (error) {
      setStatus(controller.signal.aborted ? 'cancelled' : 'failed',
        controller.signal.aborted ? null : '下载失败，请检查地址和网络。' + (typeof error.code === 'number' ? ' (' + error.code + ')' : ''),
        job.id);
    } finally {
      try { await rm(partial, { force: true }); } catch { /* A retry can overwrite this path. */ }
      active = undefined;
      void drain();
    }
  }

  function requireJob(id) {
    if (typeof id !== 'string' || !/^[a-f0-9-]{36}$/.test(id)) throw new Error('无效的下载任务');
    const job = get.get(id);
    if (!job) throw new Error('找不到下载任务');
    return job;
  }

  register('downloads:list', () => list.all());
  function enqueue(input, extension) {
    if (typeof input !== 'string' || input.length > 4096) throw new Error('无效的下载地址');
    const url = new URL(input);
    if (!protocols.has(url.protocol) || !url.hostname || url.username || url.password) throw new Error('请输入 HTTP、HTTPS 或 FTP 文件地址');
    url.hash = '';
    const previous = database.prepare(
      'SELECT id, status FROM downloads WHERE url = ? AND status IN (?, ?, ?) ORDER BY created_at DESC LIMIT 1'
    ).get(url.href, 'queued', 'running', 'completed');
    if (previous) return previous.id;
    const last = url.pathname.split('/').pop();
    const safe = (last ? decodeURIComponent(last) : '').replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').trim().replace(/[. ]+$/, '');
    let name = (!safe || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(safe) ? 'download-' : '') + (safe || 'file');
    if (['epub', 'pdf', 'txt'].includes(extension) && !/\.(epub|pdf|txt)$/i.test(name)) name += '.' + extension;
    if (name.length > 160 || name === 'partial') throw new Error('下载文件名无效');
    const id = randomUUID();
    database.prepare("INSERT INTO downloads VALUES (?, ?, ?, 'queued', NULL, ?)").run(id, url.href, name, Date.now());
    void drain();
    return id;
  }

  function waitForCompletion(id) {
    const { status } = requireJob(id);
    if (status === 'completed') return Promise.resolve();
    if (!['queued', 'running'].includes(status)) return Promise.reject(new Error('下载未完成'));
    return new Promise((resolve, reject) => {
      const listener = nextStatus => {
        if (nextStatus === 'queued' || nextStatus === 'running') return;
        events.off(id, listener);
        if (nextStatus === 'completed') resolve();
        else reject(new Error(nextStatus === 'cancelled' ? '下载已取消' : '下载失败，可在下载页重试'));
      };
      events.on(id, listener);
    });
  }

  async function preview(id) {
    await waitForCompletion(id);
    const job = requireJob(id);
    const extension = extname(job.name);
    const format = extension.slice(1).toLowerCase();
    if (!['epub', 'txt', 'pdf'].includes(format)) throw new Error('仅支持预览 EPUB、TXT 和 PDF');
    const bytes = new Uint8Array(await readFile(join(folder, id, job.name)));
    if (!bytes.length) throw new Error('下载文件为空');
    return { id, title: basename(job.name, extension), format, bytes };
  }

  register('downloads:preview', preview);
  register('downloads:fetch-and-preview', (url, extension) => preview(enqueue(url, extension)));
  register('downloads:add', enqueue);
  register('downloads:fetch-and-import', async (url, extension) => {
    const id = enqueue(url, extension);
    await waitForCompletion(id);
    const job = requireJob(id);
    return importBook(join(folder, id, job.name));
  });
  register('downloads:cancel', id => {
    const job = requireJob(id);
    if (job.status === 'queued') setStatus('cancelled', null, id);
    if (job.status === 'running' && active?.id === id) active.controller.abort();
  });
  register('downloads:retry', id => {
    const job = requireJob(id);
    if (!['failed', 'cancelled'].includes(job.status)) return;
    setStatus('queued', null, id);
    void drain();
  });
  register('downloads:import', async id => {
    const job = requireJob(id);
    if (job.status !== 'completed') throw new Error('请等待下载完成');
    return importBook(join(folder, id, job.name));
  });
  async function remove(id) {
    const job = requireJob(id);
    if (active?.id === id || ['queued', 'running'].includes(job.status)) throw new Error('请先取消下载');
    await rm(join(folder, id, job.name), { force: true });
    await rm(join(folder, id, 'partial'), { force: true });
    database.prepare('DELETE FROM downloads WHERE id = ?').run(id);
  }

  register('downloads:remove', remove);
  register('downloads:clear-completed', async () => {
    const completed = list.all().filter(job => job.status === 'completed');
    for (const job of completed) await remove(job.id);
    return completed.length;
  });
}
