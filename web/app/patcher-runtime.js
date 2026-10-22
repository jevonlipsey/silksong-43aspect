/* Silksong 4:3 patcher - browser runtime. Classic script; defines globalThis.Silk43:
 *   Silk43.load(onProgress) -> Promise<void>      idempotent; onProgress({loaded,total,label})
 *   Silk43.patch(Uint8Array) -> Promise<{ok, reason, output, log, passed, failed, skipped,
 *                                        failures, inputSha256, outputSha256, version}>
 * The .NET WebAssembly patcher runs in a dedicated Worker (patcher-worker.js) so the page stays
 * responsive. If a Worker cannot be started it runs on the main thread instead.
 */
(function () {
  'use strict';

  var scriptUrl = (document.currentScript && document.currentScript.src) || location.href;
  var base = new URL('.', scriptUrl).href;
  var loadP = null;
  var backend = null;        // { patch(bytes) -> Promise<{res, output}>, mode }
  var listeners = [];        // progress sinks (the first load() call's onProgress)

  function progress(p) {
    for (var i = 0; i < listeners.length; i++) { try { listeners[i](p); } catch (e) { /* ignore */ } }
  }

  // ---- worker backend ----
  function startWorker() {
    return new Promise(function (resolve, reject) {
      var w;
      try { w = new Worker(base + 'patcher-worker.js'); } catch (e) { reject({ startup: true, error: e }); return; }
      var pending = {};
      var nextId = 1;
      var settled = false;

      w.onerror = function (ev) {
        var err = new Error((ev && ev.message) || 'worker failed');
        if (!settled) { settled = true; w.terminate(); reject({ startup: true, error: err }); return; }
        Object.keys(pending).forEach(function (k) { pending[k].reject(err); delete pending[k]; });
      };
      w.onmessage = function (ev) {
        var m = ev.data;
        if (m.type === 'progress') { progress({ loaded: m.loaded, total: m.total, label: m.label }); }
        else if (m.type === 'ready') {
          settled = true;
          resolve({
            mode: 'worker', version: m.version,
            patch: function (bytes) {
              return new Promise(function (res, rej) {
                var id = nextId++;
                pending[id] = { resolve: res, reject: rej };
                var copy = bytes.slice().buffer;   // copy so the caller's array stays usable
                w.postMessage({ type: 'patch', id: id, bytes: copy }, [copy]);
              });
            }
          });
        }
        else if (m.type === 'initError') {
          settled = true; w.terminate();
          // the runtime itself failed to boot in the worker: let the caller try the main thread
          reject({ startup: true, error: new Error(m.message) });
        }
        else if (m.type === 'result') {
          var p = pending[m.id]; delete pending[m.id];
          if (p) p.resolve({ res: m.res, output: m.output ? new Uint8Array(m.output) : null });
        }
        else if (m.type === 'patchError') {
          var q = pending[m.id]; delete pending[m.id];
          if (q) q.reject(new Error(m.message));
        }
      };
      w.postMessage({ type: 'init' });
    });
  }

  // ---- main-thread backend (fallback) ----
  async function startMain() {
    progress({ label: 'Starting' });
    var mod = await import(base + '_framework/dotnet.js');
    var api = await mod.dotnet.withModuleConfig({
      onDownloadResourceProgress: function (loaded, total) {
        progress({ loaded: loaded, total: total, label: 'Downloading' });
      }
    }).create();
    progress({ label: 'Initializing' });
    var I = (await api.getAssemblyExports(api.getConfig().mainAssemblyName)).Interop;
    return {
      mode: 'main', version: I.GetVersion(),
      patch: async function (bytes) {
        await new Promise(function (r) { setTimeout(r, 30); });   // let the busy state paint
        var res = JSON.parse(I.Patch(bytes));
        var out = I.TakeOutput();
        return { res: res, output: out && out.length ? new Uint8Array(out) : null };
      }
    };
  }

  var Silk43 = {
    version: undefined,
    mode: undefined,

    load: function (onProgress) {
      if (onProgress) listeners.push(onProgress);
      if (loadP) return loadP;
      loadP = (async function () {
        var b;
        if (typeof Worker === 'function' && !/[?&]noworker\b/.test(location.search)) {
          try { b = await startWorker(); }
          catch (e) {
            if (!(e && e.startup)) throw e;
            if (globalThis.console) console.warn('Silk43: worker unavailable, using the main thread:', e.error);
          }
        }
        if (!b) b = await startMain();
        backend = b;
        Silk43.version = b.version;
        Silk43.mode = b.mode;
      })();
      return loadP;
    },

    patch: async function (bytes) {
      if (!(bytes instanceof Uint8Array)) bytes = new Uint8Array(bytes);
      await Silk43.load();
      var r = await backend.patch(bytes);
      var res = r.res;
      return {
        ok: !!res.ok && !!r.output,
        reason: res.reason,
        output: r.output,
        log: res.log,
        passed: res.passed,
        failed: res.failed,
        skipped: res.skipped,
        failures: res.failures || [],
        inputSha256: res.inputSha256,
        outputSha256: res.outputSha256,
        version: res.version,
        patchMillis: res.patchMillis,
        verifyMillis: res.verifyMillis
      };
    }
  };

  globalThis.Silk43 = Silk43;
})();
