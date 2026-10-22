// end-to-end: drives the real UI (web/dist/index.html, not ?mock) in headless Chrome.
//   node web/test/e2e.mjs [--dist web/dist] [--input original/Assembly-CSharp.dll] [--shot out.png] [--expect-sha <hex>]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { serve, launchChrome } from './cdp.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const argv = process.argv.slice(2);
const opt = (n, d) => { const i = argv.indexOf(n); return i >= 0 ? argv[i + 1] : d; };
const dist = path.resolve(opt('--dist', path.join(root, 'web/dist')));
const input = path.resolve(opt('--input', path.join(root, 'original/Assembly-CSharp.dll')));
const shot = opt('--shot', path.join(process.env.TMPDIR || '/tmp', 'silk43-e2e.png'));
const expect = opt('--expect-sha', null);
if (!fs.existsSync(input)) { console.error('FAIL: input missing ' + input); process.exit(1); }

const srv = await serve(dist);
const chrome = await launchChrome();
let failed = 0;
const ok = (c, m) => { console.log((c ? '  ok   ' : '  FAIL ') + m); if (!c) failed++; };
const pageErrors = [];
chrome.on('Runtime.exceptionThrown', (p) => pageErrors.push(p.exceptionDetails.exception?.description || p.exceptionDetails.text));
try {
  await chrome.goto(srv.origin + '/index.html');
  const t0 = Date.now();
  // wait for the runtime card to say ready
  await chrome.evaluate(`new Promise((res, rej) => { const t0 = Date.now(); (function w() {
    const e = document.getElementById('runtime'); if (e && e.classList.contains('ready')) return res(1);
    if (e && e.classList.contains('failed')) return rej(new Error('runtime failed: ' + document.getElementById('runtime-fail-text').textContent));
    if (Date.now() - t0 > 60000) return rej(new Error('runtime load timeout')); setTimeout(w, 100); })(); })`);
  console.log(`runtime ready after ${Date.now() - t0} ms (backend: ${await chrome.evaluate('Silk43.mode')}, ${await chrome.evaluate('document.getElementById("runtime-text").textContent')})`);

  const doc = await chrome.send('DOM.getDocument');
  const { nodeId } = await chrome.send('DOM.querySelector', { nodeId: doc.root.nodeId, selector: '#file' });
  const t1 = Date.now();
  await chrome.send('DOM.setFileInputFiles', { nodeId, files: [input] });
  const state = await chrome.evaluate(`new Promise((res) => { const t0 = Date.now(); (function w() {
    const ok = !document.getElementById('view-ok').hidden, fail = !document.getElementById('view-fail').hidden;
    if (ok || fail || Date.now() - t0 > 120000) return res(ok ? 'ok' : fail ? 'fail' : 'timeout'); setTimeout(w, 200); })(); })`);
  console.log(`result state: ${state} after ${Date.now() - t1} ms`);
  ok(state === 'ok', 'success view is shown');
  const info = JSON.parse(await chrome.evaluate(`(async () => {
    const a = document.getElementById('download'); const r = await fetch(a.href); const b = new Uint8Array(await r.arrayBuffer());
    const hex = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', b)), (x) => x.toString(16).padStart(2, '0')).join('');
    return JSON.stringify({ visible: !a.closest('[hidden]') && a.offsetParent !== null, href: a.href.slice(0, 5), name: a.getAttribute('download'),
      size: b.length, blobSha: hex, outHash: document.getElementById('ok-out-hash').textContent.trim(),
      inHash: document.getElementById('ok-in-hash').textContent.trim(), verified: document.getElementById('ok-verified').textContent.trim() });
  })()`));
  console.log(JSON.stringify(info));
  ok(info.visible && info.href === 'blob:', 'download button visible with a blob: url');
  ok(info.size > 1000000, `download blob has ${info.size} bytes`);
  ok(info.blobSha === info.outHash.replace(/\s+/g, '').toLowerCase(), 'blob sha256 equals the hash shown in the UI');
  if (expect) ok(info.blobSha === expect, 'blob sha256 equals the expected CLI hash');
  const png = await chrome.send('Page.captureScreenshot', { format: 'png' });
  fs.writeFileSync(shot, Buffer.from(png.data, 'base64'));
  console.log('screenshot ' + shot);
  ok(pageErrors.length === 0, 'no uncaught page exceptions' + (pageErrors.length ? ': ' + pageErrors.join('; ') : ''));
} catch (e) { console.log('  FAIL ' + e.message); failed++; }
finally { await chrome.close(); srv.close(); }
console.log(failed ? 'E2E FAILED' : 'E2E OK');
process.exit(failed ? 1 : 0);
