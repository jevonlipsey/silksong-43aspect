// parity test: the WebAssembly patcher must produce byte-identical output to the CLI patcher.
//   node web/test/parity.mjs [--dist web/dist] [--input file]... [--chrome] [--no-node]
// env: SILK43_DIST, SILK43_INPUTS (path-delimited list), CHROME (chrome binary).
// Node mode boots the browser bundle straight from disk (fast, no browser needed). --chrome also drives
// headless Chrome through the real patcher-runtime.js (Silk43.load / Silk43.patch, Worker backend and
// the ?noworker main-thread fallback).
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';
import { serve, launchChrome } from './cdp.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const argv = process.argv.slice(2);
const flag = (n) => argv.includes(n);
const opt = (n) => { const r = []; argv.forEach((a, i) => { if (a === n) r.push(argv[i + 1]); }); return r; };

const dist = path.resolve(opt('--dist')[0] || process.env.SILK43_DIST || path.join(root, 'web/dist'));
let inputs = opt('--input');
if (!inputs.length && process.env.SILK43_INPUTS) inputs = process.env.SILK43_INPUTS.split(path.delimiter);
if (!inputs.length) inputs = [path.join(root, 'original/Managed/Assembly-CSharp.dll')];
const present = inputs.filter((p) => { if (fs.existsSync(p)) return true; console.warn(`WARN: missing input ${p}, skipping`); return false; });
if (!present.length) { console.error('FAIL: no input assemblies present'); process.exit(1); }
if (!fs.existsSync(path.join(dist, '_framework/dotnet.js'))) { console.error(`FAIL: ${dist}/_framework/dotnet.js not found (run tools/build_web.sh)`); process.exit(1); }

const sha = (b) => crypto.createHash('sha256').update(b).digest('hex');
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'silk43-parity-'));
let failures = 0;
const ok = (cond, msg) => { if (cond) console.log(`  ok   ${msg}`); else { failures++; console.log(`  FAIL ${msg}`); } };

// ---- reference: the CLI with no managed dir (reference-free verify) ----
const refs = {};
for (const input of present) {
  const out = path.join(scratch, `ref-${path.basename(input)}`);
  const t0 = Date.now();
  const r = spawnSync('dotnet', ['run', '--project', 'patcher', '-c', 'Release', '--', input, '-', out], { cwd: root, encoding: 'utf8' });
  if (r.status !== 0 || !fs.existsSync(out)) { console.error(r.stdout, r.stderr); console.error(`FAIL: CLI could not patch ${input}`); process.exit(1); }
  refs[input] = sha(fs.readFileSync(out));
  console.log(`CLI reference ${path.basename(input)}: ${refs[input].slice(0, 16)}... (${Date.now() - t0} ms)`);
}
const junk = crypto.randomBytes(4096);

// ---- a backend is { patch(bytes) -> { reason, ok, failed, passed, skipped, output|null, outputSha256, patchMillis, verifyMillis, ms, outputActualSha } } ----
async function runSuite(name, backend) {
  console.log(`\n== ${name}`);
  for (const input of present) {
    const label = path.basename(input);
    const bytes = fs.readFileSync(input);
    const r = await backend.patch(bytes);
    console.log(`  ${label}: patch ${r.ms} ms (cecil ${r.patchMillis} ms, verify ${r.verifyMillis} ms), ${r.passed} passed ${r.failed} failed ${r.skipped} skipped`);
    ok(r.reason === 'ok', `${label}: reason ok (got ${r.reason})`);
    ok(r.failed === 0, `${label}: 0 failed checks`);
    ok(r.outputActualSha === refs[input], `${label}: output byte-identical to the CLI (${(r.outputActualSha || '').slice(0, 16)}...)`);
    ok(r.outputSha256 === r.outputActualSha, `${label}: reported outputSha256 matches the returned bytes`);
    ok(r.inputSha256 === sha(bytes), `${label}: inputSha256 matches`);
    if (r.reason === 'ok') {
      const again = await backend.patch(r.outputBytes());
      ok(again.reason === 'already_patched' && again.outputSize === 0, `${label}: patching the output again -> already_patched, no output (got ${again.reason})`);
    }
  }
  const j = await backend.patch(junk);
  ok(j.reason === 'not_silksong' && j.outputSize === 0, `junk buffer -> not_silksong (got ${j.reason})`);
}

// ---- node backend ----
async function nodeBackend() {
  const t0 = Date.now();
  const { dotnet } = await import(pathToFileURL(path.join(dist, '_framework/dotnet.js')));
  const api = await dotnet.create();
  const I = (await api.getAssemblyExports(api.getConfig().mainAssemblyName)).Interop;
  console.log(`\nNode: runtime load ${Date.now() - t0} ms, version ${I.GetVersion()}`);
  return {
    patch(bytes) {
      const t = Date.now();
      const res = JSON.parse(I.Patch(new Uint8Array(bytes)));
      const out = I.TakeOutput();
      const outCopy = out && out.length ? Buffer.from(out) : null;
      return Promise.resolve({ ...res, ms: Date.now() - t, outputSize: outCopy ? outCopy.length : 0,
        outputActualSha: outCopy ? sha(outCopy) : null, outputBytes: () => outCopy });
    },
  };
}

if (!flag('--no-node')) await runSuite('Node (browser bundle booted from disk)', await nodeBackend());

// ---- chrome backend: the real patcher-runtime.js ----
if (flag('--chrome')) {
  const extra = {}; present.forEach((p, i) => { extra[`/__in/${i}`] = p; });
  const srv = await serve(dist, extra);
  const chrome = await launchChrome();
  try {
    for (const mode of ['', '?noworker']) {
      await chrome.goto(`${srv.origin}/index.html${mode}`);
      const t0 = Date.now();
      const info = await chrome.evaluate(`(async () => {
        const t0 = performance.now(); const prog = [];
        await Silk43.load((p) => prog.push(p));
        return JSON.stringify({ load: Math.round(performance.now()), mode: Silk43.mode, version: Silk43.version,
          progressCalls: prog.length, lastProgress: prog[prog.length - 1] || null, hasBytes: prog.some((p) => p.total > 0) });
      })()`).then(JSON.parse);
      console.log(`\nChrome ${mode || '(worker)'}: runtime ready ${info.load} ms after navigation (cold cache), backend=${info.mode}, version=${info.version}, progress calls=${info.progressCalls} (byte/count progress: ${info.hasBytes}), last=${JSON.stringify(info.lastProgress)}`);
      ok(info.mode === (mode ? 'main' : 'worker'), `backend is ${mode ? 'main thread' : 'worker'} (got ${info.mode})`);
      const idx = { n: 0 };   // each output is parked in the page (globalThis.__outs) so the re-patch can reuse it
      const backend = {
        async patch(bytes) {
          // identify the input: the known inputs are served by index, anything else is sent inline
          const k = present.findIndex((p) => fs.readFileSync(p).equals(bytes));
          const src = k >= 0 ? `new Uint8Array(await (await fetch('/__in/${k}')).arrayBuffer())`
            : `Uint8Array.from(atob('${Buffer.from(bytes).toString('base64')}'), (c) => c.charCodeAt(0))`;
          const hold = `globalThis.__outs = globalThis.__outs || {}`;
          const r = await chrome.evaluate(`(async () => { ${hold};
            const hex = (b) => Array.from(new Uint8Array(b), (x) => x.toString(16).padStart(2, '0')).join('');
            const input = ${src}; const t = performance.now();
            const r = await Silk43.patch(input); const ms = Math.round(performance.now() - t);
            const key = ${++idx.n};
            if (r.output) globalThis.__outs[key] = r.output;
            return JSON.stringify({ reason: r.reason, ok: r.ok, failed: r.failed, passed: r.passed, skipped: r.skipped,
              inputSha256: r.inputSha256, outputSha256: r.outputSha256, patchMillis: r.patchMillis, verifyMillis: r.verifyMillis,
              ms, key, outputSize: r.output ? r.output.length : 0,
              outputActualSha: r.output ? hex(await crypto.subtle.digest('SHA-256', r.output)) : null,
              logHasVerify: typeof r.log === 'string' && r.log.length > 1000, inputUntouched: input.length === ${bytes.length} });
          })()`).then(JSON.parse);
          r.outputBytes = () => ({ __chromeKey: r.key });
          return r;
        },
      };
      // chrome re-patch needs the output bytes inside the page, not in node
      const origPatch = backend.patch;
      backend.patch = async (bytes) => {
        if (bytes && bytes.__chromeKey) {
          const k = bytes.__chromeKey;
          const r = await chrome.evaluate(`(async () => {
            const hex = (b) => Array.from(new Uint8Array(b), (x) => x.toString(16).padStart(2, '0')).join('');
            const t = performance.now(); const r = await Silk43.patch(globalThis.__outs[${k}]);
            return JSON.stringify({ reason: r.reason, ok: r.ok, failed: r.failed, passed: r.passed, skipped: r.skipped,
              ms: Math.round(performance.now() - t), outputSize: r.output ? r.output.length : 0 });
          })()`).then(JSON.parse);
          return r;
        }
        return origPatch(bytes);
      };
      await runSuite(`Chrome ${mode || '(worker)'} via patcher-runtime.js`, backend);
    }
  } finally { await chrome.close(); srv.close(); }
}

fs.rmSync(scratch, { recursive: true, force: true });
console.log(failures ? `\nFAILED: ${failures} assertion(s)` : '\nPARITY OK');
process.exit(failures ? 1 : 0);
