function webUrl(input) {
  if (typeof input !== 'string' || input.length > 4096) throw new Error('无效的书源地址');
  const url = new URL(input);
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) {
    throw new Error('请输入 HTTP 或 HTTPS 地址');
  }
  url.hash = '';
  return url;
}

export function registerFeeds(register, database, openExternal) {
  database.exec(`
    CREATE TABLE IF NOT EXISTS feeds (
      url TEXT PRIMARY KEY, title TEXT NOT NULL, content TEXT NOT NULL,
      updated_at INTEGER NOT NULL
    )
  `);
  register('feeds:list', () =>
    database.prepare('SELECT url, title, content FROM feeds ORDER BY updated_at DESC, url').all()
  );
  register('feeds:fetch', async input => {
    let url = webUrl(input);
    const signal = AbortSignal.timeout(15000);
    for (let hops = 0; hops <= 5; hops++) {
      const response = await fetch(url, {
        signal, redirect: 'manual',
        headers: { Accept: 'application/atom+xml, application/rss+xml, application/xml, text/xml, */*;q=0.5' }
      });
      if ([301, 302, 303, 307, 308].includes(response.status)) {
        const target = response.headers.get('location');
        await response.body?.cancel();
        if (!target) throw new Error('书源重定向缺少地址');
        url = webUrl(new URL(target, url).href);
        continue;
      }
      if (!response.ok || !response.body) {
        await response.body?.cancel();
        throw new Error('书源请求失败 (' + response.status + ')');
      }
      const maxBytes = 4 * 1024 * 1024;
      const chunks = [];
      let size = 0;
      const reader = response.body.getReader();
      try {
        while (true) {
          const { value, done } = await reader.read();
          if (done) break;
          size += value.byteLength;
          if (size > maxBytes) throw new Error('书源响应过大');
          chunks.push(value);
        }
      } finally {
        await reader.cancel().catch(() => {});
        reader.releaseLock();
      }
      const bytes = Buffer.concat(chunks, size);
      const charset = response.headers.get('content-type')?.match(/charset=([^;\s]+)/i)?.[1]?.replace(/["']/g, '') ??
        bytes.subarray(0, 160).toString('latin1').match(/encoding=["']([^"']+)["']/i)?.[1] ?? 'utf-8';
      return { url: url.href, xml: new TextDecoder(charset).decode(bytes) };
    }
    throw new Error('书源重定向次数过多');
  });
  register('feeds:save', (input, title, content) => {
    const url = webUrl(input).href;
    if (typeof title !== 'string' || title.length > 4096 || typeof content !== 'string' || content.length > 8 * 1024 * 1024) {
      throw new Error('无效的书源内容');
    }
    database.prepare(`
      INSERT INTO feeds VALUES (?, ?, ?, ?)
      ON CONFLICT(url) DO UPDATE SET title = excluded.title, content = excluded.content, updated_at = excluded.updated_at
    `).run(url, title, content, Date.now());
  });
  register('feeds:remove', input => {
    database.prepare('DELETE FROM feeds WHERE url = ?').run(webUrl(input).href);
  });
  register('feeds:external', input => openExternal(webUrl(input).href));
}
