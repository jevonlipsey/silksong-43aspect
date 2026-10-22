/* Dedicated worker that hosts the .NET WebAssembly patcher (classic worker; uses dynamic import).
 * Messages in:  { type: 'init' } | { type: 'patch', id, bytes: ArrayBuffer }
 * Messages out: { type: 'progress', loaded, total, label } | { type: 'ready', version }
 *               | { type: 'initError', message } | { type: 'result', id, res, output: ArrayBuffer|null }
 *               | { type: 'patchError', id, message }
 */
'use strict';
var exportsP = null;

function boot() {
  if (exportsP) return exportsP;
  exportsP = (async function () {
    postMessage({ type: 'progress', label: 'Starting' });
    var mod = await import('./_framework/dotnet.js');
    var builder = mod.dotnet.withModuleConfig({
      onDownloadResourceProgress: function (loaded, total) {
        postMessage({ type: 'progress', loaded: loaded, total: total, label: 'Downloading' });
      }
    });
    var api = await builder.create();
    postMessage({ type: 'progress', label: 'Initializing' });
    var ex = await api.getAssemblyExports(api.getConfig().mainAssemblyName);
    return ex.Interop;
  })();
  return exportsP;
}

// addEventListener, not self.onmessage: dotnet.js treats a worker whose global onmessage is set as a
// runtime pthread and then never finishes booting (it only boots normally when onmessage is unset)
self.addEventListener('message', function (e) {
  var m = e.data;
  if (m.type === 'init') {
    boot().then(function (I) {
      postMessage({ type: 'ready', version: I.GetVersion() });
    }, function (err) {
      postMessage({ type: 'initError', message: String((err && err.message) || err) });
    });
  } else if (m.type === 'patch') {
    boot().then(function (I) {
      var res = JSON.parse(I.Patch(new Uint8Array(m.bytes)));
      var out = I.TakeOutput();
      var buf = null;
      if (out && out.length) { buf = new Uint8Array(out).buffer; }
      postMessage({ type: 'result', id: m.id, res: res, output: buf }, buf ? [buf] : []);
    }).catch(function (err) {
      postMessage({ type: 'patchError', id: m.id, message: String((err && err.message) || err) });
    });
  }
});
