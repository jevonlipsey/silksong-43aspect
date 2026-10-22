// minimal headless-Chrome driver over the DevTools protocol (no dependencies: Node's built-in
// WebSocket and fetch) plus a static file server for web/dist. shared by parity.mjs and e2e.mjs.
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';

const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css',
  '.wasm': 'application/wasm', '.json': 'application/json', '.dll': 'application/octet-stream',
};

/// serves distDir at /, plus extra { '/path': absoluteFile } entries
export function serve(distDir, extra = {}) {
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://x');
    let file = extra[url.pathname];
    if (!file) {
      const rel = path.normalize(decodeURIComponent(url.pathname)).replace(/^(\.\.[/\\])+/, '');
      file = path.join(distDir, rel === path.sep || rel === '.' ? 'index.html' : rel);
    }
    fs.readFile(file, (err, buf) => {
      if (err) { res.writeHead(404); res.end('not found'); return; }
      res.writeHead(200, { 'content-type': MIME[path.extname(file)] || 'application/octet-stream', 'cache-control': 'no-store' });
      res.end(buf);
    });
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () =>
    resolve({ server, origin: `http://127.0.0.1:${server.address().port}`, close: () => server.close() })));
}

export function findChrome() {
  const c = [process.env.CHROME,
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/usr/bin/google-chrome', '/usr/bin/google-chrome-stable', '/usr/bin/chromium', '/usr/bin/chromium-browser']
    .filter(Boolean);
  return c.find((p) => fs.existsSync(p));
}

export async function launchChrome(extraArgs = []) {
  const exe = findChrome();
  if (!exe) throw new Error('Chrome not found (set CHROME=/path/to/chrome)');
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'silk43-chrome-'));
  const proc = spawn(exe, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
    '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--no-sandbox', '--window-size=1100,900',
    ...extraArgs, 'about:blank'], { stdio: ['ignore', 'ignore', 'pipe'] });
  const port = await new Promise((resolve, reject) => {
    let buf = '';
    const t = setTimeout(() => reject(new Error('chrome did not start')), 30000);
    proc.stderr.on('data', (d) => {
      buf += d;
      const m = /DevTools listening on ws:\/\/127\.0\.0\.1:(\d+)\//.exec(buf);
      if (m) { clearTimeout(t); resolve(+m[1]); }
    });
    proc.on('exit', (c) => reject(new Error('chrome exited ' + c)));
  });
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const page = targets.find((t) => t.type === 'page');
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
  let id = 0;
  const waiting = new Map();
  const handlers = new Map();
  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && waiting.has(m.id)) {
      const { resolve, reject } = waiting.get(m.id); waiting.delete(m.id);
      m.error ? reject(new Error(m.error.message)) : resolve(m.result);
    } else if (m.method && handlers.has(m.method)) {
      for (const h of handlers.get(m.method)) h(m.params);
    }
  };
  const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => {
    const i = ++id; waiting.set(i, { resolve, reject }); ws.send(JSON.stringify({ id: i, method, params, ...(sessionId ? { sessionId } : {}) }));
  });
  const on = (method, h) => { if (!handlers.has(method)) handlers.set(method, []); handlers.get(method).push(h); };
  await send('Page.enable'); await send('Runtime.enable'); await send('DOM.enable');
  return {
    send, on,
    async evaluate(expression) {
      const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
      if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
      return r.result.value;
    },
    async goto(url) {
      const loaded = new Promise((r) => on('Page.loadEventFired', r));
      await send('Page.navigate', { url });
      await loaded;
    },
    async close() {
      try { ws.close(); } catch {}
      proc.kill('SIGKILL');
      try { fs.rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch {}
    },
  };
}
