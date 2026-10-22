/* Silksong 4:3 Patcher - page logic.
 *
 * Talks to the patcher through globalThis.Silk43 (provided by patcher-runtime.js):
 *   Silk43.load(onProgress) -> Promise<void>
 *   Silk43.patch(Uint8Array) -> Promise<{ok, reason, output, log, passed, failed, skipped,
 *                                        failures, inputSha256, outputSha256, version}>
 * Add ?mock to the URL to use mock-runtime.js instead of the real runtime (dev only).
 */
(function () {
  'use strict';

  var GUIDE_URL = 'https://github.com/jevonlipsey/silksong-43aspect/blob/main/README.md';
  var ISSUES_URL = 'https://github.com/jevonlipsey/silksong-43aspect/issues/new';
  // transfer size of _framework/ (about 2.1 MB gzipped), shown to the user while preloading.
  var RUNTIME_MB = 2;
  // Refuse absurd files before reading them into WebAssembly memory.
  var MAX_BYTES = 200 * 1024 * 1024;

  function $(id) { return document.getElementById(id); }
  var el = {
    status: $('status'), version: $('version'),
    runtime: $('runtime'), runtimeText: $('runtime-text'), runtimeBar: $('runtime-bar'),
    runtimeFail: $('runtime-fail'), runtimeFailText: $('runtime-fail-text'),
    pick: $('view-pick'), drop: $('drop'), file: $('file'),
    busy: $('view-busy'), busyTitle: $('busy-title'), busyFile: $('busy-file'),
    busyNote: $('busy-note'), busyElapsed: $('busy-elapsed'),
    ok: $('view-ok'), okTitle: $('ok-title'), okVerified: $('ok-verified'),
    okIn: $('ok-in-hash'), okOut: $('ok-out-hash'),
    download: $('download'), downloadSize: $('download-size'),
    fail: $('view-fail'), failTitle: $('fail-title'), failTitleText: $('fail-title-text'),
    failBody: $('fail-body'), failFailures: $('fail-failures'), failCount: $('fail-count'),
    failList: $('fail-list'), failHashes: $('fail-hashes'), failIn: $('fail-in-hash'),
    copy: $('copy-report'), issues: $('issues-link'), fallback: $('report-fallback'),
    failGuide: $('fail-guide'),
    logWrap: $('log-wrap'), log: $('log')
  };

  var loadState = 'loading';   // 'loading' | 'ready' | 'failed'
  var loadPromise = null;      // resolves true/false, never rejects
  var runId = 0;               // invalidates in-flight work after a reset
  var downloadUrl = null;
  var elapsedTimer = null;
  var lastReport = '';
  var version = '';

  // ---------- small helpers ----------

  function announce(msg) {
    // Clear first so repeating the same text is still announced.
    el.status.textContent = '';
    setTimeout(function () { el.status.textContent = msg; }, 30);
  }

  function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }

  function make(tag, text, cls) {
    var n = document.createElement(tag);
    if (text != null) n.textContent = text;
    if (cls) n.className = cls;
    return n;
  }

  function fmtBytes(n) {
    if (n >= 1048576) return (n / 1048576).toFixed(1) + ' MB';
    if (n >= 1024) return Math.round(n / 1024) + ' KB';
    return n + ' bytes';
  }

  function errText(e) { return (e && e.message) ? String(e.message) : String(e); }

  function setVersion(v) {
    if (!v) return;
    version = String(v);
    el.version.textContent = /^v/i.test(version) ? version : 'v' + version;
    el.version.hidden = false;
  }

  function show(view) {
    ['pick', 'busy', 'ok', 'fail'].forEach(function (k) { el[k].hidden = (k !== view); });
    if (view !== 'ok' && view !== 'fail') el.logWrap.hidden = true;
  }

  function stopElapsed() { if (elapsedTimer) { clearInterval(elapsedTimer); elapsedTimer = null; } }

  function revokeDownload() {
    if (downloadUrl) { try { URL.revokeObjectURL(downloadUrl); } catch (e) { /* ignore */ } downloadUrl = null; }
    el.download.removeAttribute('href');
  }

  function focusHeading(h) {
    try { h.focus({ preventScroll: false }); } catch (e) { h.focus(); }
    if (h.scrollIntoView) h.scrollIntoView({ block: 'nearest' });
  }

  // ---------- links ----------

  Array.prototype.forEach.call(document.querySelectorAll('.guide-link'), function (a) { a.href = GUIDE_URL; });
  el.issues.href = ISSUES_URL;

  // ---------- runtime preload ----------

  function setRuntimeProgress(p) {
    if (loadState !== 'loading') return;
    var label = p && p.label ? String(p.label) : '';
    var loaded = p && +p.loaded, total = p && +p.total;
    var base = 'Loading patcher (~' + RUNTIME_MB + ' MB, once)';
    var bar = el.runtimeBar, fill = bar.firstElementChild;
    if (total > 0 && loaded >= 0) {
      var pct = Math.max(0, Math.min(100, Math.round(loaded / total * 100)));
      bar.classList.remove('indeterminate');
      fill.style.width = pct + '%';
      bar.setAttribute('aria-valuenow', String(pct));
      el.runtimeText.textContent = base + ' ' + pct + '%' + (label ? ' - ' + label : '');
    } else {
      bar.classList.add('indeterminate');
      fill.style.width = '';
      bar.removeAttribute('aria-valuenow');
      el.runtimeText.textContent = base + (label ? ' - ' + label : '...');
    }
  }

  function runtimeFailed(why, detail) {
    loadState = 'failed';
    el.runtime.classList.remove('ready');
    el.runtime.classList.add('failed');
    el.runtimeText.textContent = 'The patcher could not start.';
    clear(el.runtimeFailText);
    el.runtimeFailText.appendChild(document.createTextNode(why + ' '));
    el.runtimeFailText.appendChild(document.createTextNode(
      'You can use the desktop patcher instead (see the install guide for the download). '));
    var a = make('a', 'Open the install guide');
    a.href = GUIDE_URL; a.target = '_blank'; a.rel = 'noopener';
    el.runtimeFailText.appendChild(a);
    if (detail) {
      el.runtimeFailText.appendChild(make('br'));
      el.runtimeFailText.appendChild(make('code', detail));
    }
    el.runtimeFail.hidden = false;
    el.file.disabled = true;
    el.drop.classList.add('disabled');
    announce('The patcher could not start. ' + why);
  }

  function runtimeReady() {
    loadState = 'ready';
    el.runtime.classList.add('ready');
    el.runtime.hidden = true;
    el.runtimeBar.setAttribute('aria-valuenow', '100');
    try { if (globalThis.Silk43 && globalThis.Silk43.version) setVersion(globalThis.Silk43.version); } catch (e) { /* optional */ }
    announce('Patcher ready.');
  }

  function startRuntime() {
    var C = globalThis.Silk43;
    if (!C || typeof C.load !== 'function' || typeof C.patch !== 'function') {
      runtimeFailed('The patcher runtime file (patcher-runtime.js) did not load, so the page has nothing to patch with. ' +
        'This usually means the site was deployed incompletely or a content blocker removed the script.');
      return Promise.resolve(false);
    }
    setRuntimeProgress({});
    var supportsWasm = typeof WebAssembly === 'object';
    if (!supportsWasm) {
      runtimeFailed('This browser does not support WebAssembly. Try a current version of Chrome, Firefox, Safari or Edge.');
      return Promise.resolve(false);
    }
    // Call load() exactly once; every consumer awaits this same promise.
    return Promise.resolve().then(function () { return C.load(setRuntimeProgress); }).then(function () {
      runtimeReady();
      return true;
    }, function (e) {
      runtimeFailed('The patcher failed to load. Your browser may be too old, low on memory, or offline before the first load.', errText(e));
      return false;
    });
  }

  // ---------- file handling ----------

  function readFile(file) {
    return new Promise(function (resolve, reject) {
      if (file.arrayBuffer) { file.arrayBuffer().then(resolve, reject); return; }
      var r = new FileReader();
      r.onload = function () { resolve(r.result); };
      r.onerror = function () { reject(r.error || new Error('Could not read the file.')); };
      r.readAsArrayBuffer(file);
    });
  }

  function showBusy(file, waiting) {
    show('busy');
    el.busyTitle.textContent = waiting ? 'Waiting for the patcher to load…' : 'Patching…';
    el.busyFile.textContent = file.name + ' (' + fmtBytes(file.size) + ')';
    el.busyNote.textContent = waiting
      ? 'Your file is queued and will be patched as soon as the patcher has finished loading (progress is shown above).'
      : 'This can take a while, up to a minute or more on a phone. The page is working, so please keep this tab open.';
    var t0 = Date.now();
    stopElapsed();
    el.busyElapsed.textContent = '0s';
    elapsedTimer = setInterval(function () {
      var s = Math.floor((Date.now() - t0) / 1000);
      el.busyElapsed.textContent = s < 60 ? s + 's' : Math.floor(s / 60) + 'm ' + (s % 60) + 's';
    }, 1000);
    announce(waiting ? 'File queued. Waiting for the patcher to load.' : 'Patching started. This can take a while.');
  }

  function handleFile(file) {
    if (!file || loadState === 'failed') return;
    var my = ++runId;
    var bytes;

    if (file.size === 0) { showFail(failFromUi('error', 'That file is empty.', file.name)); return; }
    if (file.size > MAX_BYTES) { showFail(failFromUi('error', 'That file is too large (' + fmtBytes(file.size) + ') to be Assembly-CSharp.dll.', file.name)); return; }

    showBusy(file, loadState !== 'ready');

    readFile(file).then(function (buf) {
      bytes = new Uint8Array(buf);
      return loadPromise;
    }).then(function (ok) {
      if (my !== runId) return null;
      if (!ok) { stopElapsed(); show('pick'); return null; }   // runtime card already explains why
      el.busyTitle.textContent = 'Patching…';
      el.busyNote.textContent = 'This can take a while, up to a minute or more on a phone. The page is working, so please keep this tab open.';
      announce('Patching started. This can take a while.');
      // Give the browser a frame to paint the busy state before heavy work starts.
      return new Promise(function (r) { setTimeout(r, 50); }).then(function () {
        return globalThis.Silk43.patch(bytes);
      }).then(function (res) {
        if (my !== runId) return null;
        stopElapsed();
        showResult(res || {}, file);
      });
    }).catch(function (e) {
      if (my !== runId) return;
      stopElapsed();
      showFail(failFromUi('error', 'The patcher threw an unexpected error: ' + errText(e), file.name, errText(e)));
    });
  }

  // A synthetic result for failures that happen in the page itself.
  function failFromUi(reason, message, fileName, log) {
    return { ok: false, reason: reason, output: null, log: log || message, passed: 0, failed: 0, skipped: 0,
      failures: [], inputSha256: '', outputSha256: null, version: version, _uiMessage: message, _file: fileName };
  }

  // ---------- results ----------

  function showResult(res, file) {
    if (res.version) setVersion(res.version);
    if (res.ok === true && res.reason === 'ok' && res.output && res.output.length) { showOk(res); return; }
    if (res.ok === true) {   // contract violation, don't hand out an empty file
      res = failFromUi('error', 'The patcher reported success but returned no output file.', file && file.name, res.log);
    }
    showFail(res);
  }

  function setLog(text) {
    if (text) { el.log.textContent = text; el.logWrap.hidden = false; el.logWrap.open = false; }
    else { el.log.textContent = ''; el.logWrap.hidden = true; }
  }

  function showOk(res) {
    revokeDownload();
    var blob = new Blob([res.output], { type: 'application/octet-stream' });
    downloadUrl = URL.createObjectURL(blob);
    el.download.href = downloadUrl;
    el.downloadSize.textContent = fmtBytes(blob.size);

    var passed = res.passed | 0, skipped = res.skipped | 0;
    el.okVerified.textContent = 'Verified: ' + passed + ' check' + (passed === 1 ? '' : 's') + ' passed' +
      (skipped > 0 ? ' (' + skipped + ' skipped — these need the Unity assemblies and run in the desktop patcher)' : '') + '.';
    el.okIn.textContent = res.inputSha256 || 'unavailable';
    el.okOut.textContent = res.outputSha256 || 'unavailable';
    setLog(res.log);
    show('ok');
    announce('Done. ' + el.okVerified.textContent + ' Your patched file is ready to download.');
    focusHeading(el.okTitle);
  }

  var COPY = {
    already_patched: {
      title: 'This file is already patched.',
      body: ['To patch it again, restore your backup (Assembly-CSharp.dll.bak) or use Steam\'s “Verify integrity of game files” to get the original back, then choose that original file.']
    },
    not_silksong: {
      title: 'This doesn\'t look like Silksong\'s Assembly-CSharp.dll.',
      body: ['Check that you picked the Assembly-CSharp.dll inside Silksong\'s Managed folder (the paths are listed in step 1), and not a different game\'s file or another DLL.']
    },
    verify_failed: {
      title: 'This version of the game isn\'t supported yet.',
      body: ['The game has changed in a way this patcher version doesn\'t support yet. Nothing was produced, so your game is safe.',
        'If you report it, use “Copy report” and paste it into the issue so it can be fixed.']
    },
    error: {
      title: 'Something went wrong.',
      body: ['The patcher hit an unexpected error and stopped. Nothing was produced, so your game is safe.']
    }
  };

  function buildReport(res) {
    var lines = [];
    lines.push('Silksong 4:3 Patcher ' + (res.version || version || '(unknown version)'));
    lines.push('Result: ' + res.reason);
    lines.push('Input SHA-256: ' + (res.inputSha256 || 'unavailable'));
    lines.push('Checks: ' + (res.passed | 0) + ' passed, ' + (res.failed | 0) + ' failed, ' + (res.skipped | 0) + ' skipped');
    lines.push('Browser: ' + (navigator.userAgent || 'unknown'));
    var f = res.failures || [];
    lines.push('');
    lines.push('Failures (' + f.length + '):');
    if (f.length) f.forEach(function (x) { lines.push('- ' + x); }); else lines.push('(none)');
    lines.push('');
    lines.push('Log:');
    lines.push(res.log || '(empty)');
    return lines.join('\n');
  }

  function showFail(res) {
    revokeDownload();
    var reason = COPY[res.reason] ? res.reason : 'error';
    var c = COPY[reason];
    el.failTitleText.textContent = c.title;
    clear(el.failBody);
    c.body.forEach(function (t) { el.failBody.appendChild(make('p', t)); });
    if (reason === 'error' && res._uiMessage) el.failBody.appendChild(make('p', res._uiMessage));
    if (reason === 'error' && !res._uiMessage && res.log) {
      el.failBody.appendChild(make('p', 'The full log is below; please include it if you report this.'));
    }
    if (reason === 'already_patched' || reason === 'not_silksong') {
      var p = make('p'); var a = make('a', 'Full install guide');
      a.href = GUIDE_URL; a.target = '_blank'; a.rel = 'noopener';
      p.appendChild(a); el.failBody.appendChild(p);
    }

    var failures = Array.isArray(res.failures) ? res.failures : [];
    clear(el.failList);
    failures.forEach(function (f) { el.failList.appendChild(make('li', String(f))); });
    el.failFailures.hidden = !(reason === 'verify_failed' && failures.length);
    el.failCount.textContent = '(' + failures.length + ')';

    el.failIn.textContent = res.inputSha256 || '';
    el.failHashes.hidden = !res.inputSha256;

    var reportable = reason === 'verify_failed' || reason === 'error';
    lastReport = buildReport(res);
    el.copy.hidden = !reportable;
    el.copy.textContent = 'Copy report';
    el.issues.hidden = !reportable;
    el.fallback.hidden = true;
    el.failGuide.hidden = reportable;   // reportable failures already link the issue tracker

    setLog(res.log);
    show('fail');
    announce(c.title + (res.reason === 'verify_failed' ? ' Nothing was produced.' : ''));
    focusHeading(el.failTitle);
  }

  // ---------- clipboard ----------

  function copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText && window.isSecureContext !== false) {
      return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return legacyCopy(text); });
    }
    return Promise.resolve(legacyCopy(text));
  }

  function legacyCopy(text) {
    var ta = el.fallback;
    ta.value = text;
    ta.hidden = false;
    ta.focus();
    ta.select();
    try { ta.setSelectionRange(0, text.length); } catch (e) { /* ignore */ }
    var ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    if (ok) ta.hidden = true;   // otherwise leave it visible and selected for manual copy
    return ok;
  }

  el.copy.addEventListener('click', function () {
    Promise.resolve(copyText(lastReport)).then(function (ok) {
      el.copy.textContent = ok ? 'Copied!' : 'Select the text below and copy it';
      announce(ok ? 'Report copied to the clipboard.' : 'Could not copy automatically. The report is selected below; copy it manually.');
      if (ok) setTimeout(function () { el.copy.textContent = 'Copy report'; }, 2500);
    });
  });

  // ---------- reset ----------

  function reset() {
    runId++;
    stopElapsed();
    revokeDownload();
    el.file.value = '';
    el.fallback.hidden = true;
    lastReport = '';
    setLog('');
    show('pick');
    announce('Ready for another file.');
    try { el.file.focus(); } catch (e) { /* ignore */ }
  }
  Array.prototype.forEach.call(document.querySelectorAll('[data-action="reset"]'), function (b) {
    b.addEventListener('click', reset);
  });

  // ---------- input wiring ----------

  el.file.addEventListener('change', function () {
    var f = el.file.files && el.file.files[0];
    if (f) handleFile(f);
  });

  function hasFiles(e) {
    var t = e.dataTransfer && e.dataTransfer.types;
    if (!t) return false;
    for (var i = 0; i < t.length; i++) if (t[i] === 'Files') return true;
    return false;
  }

  // Stop the browser from navigating to a file dropped outside the drop zone.
  window.addEventListener('dragover', function (e) { if (hasFiles(e)) e.preventDefault(); });
  window.addEventListener('drop', function (e) { if (hasFiles(e)) e.preventDefault(); });

  var dragDepth = 0;
  el.drop.addEventListener('dragenter', function (e) {
    if (!hasFiles(e) || el.file.disabled) return;
    dragDepth++; el.drop.classList.add('over');
  });
  el.drop.addEventListener('dragleave', function () {
    dragDepth = Math.max(0, dragDepth - 1);
    if (!dragDepth) el.drop.classList.remove('over');
  });
  el.drop.addEventListener('dragover', function (e) {
    if (!hasFiles(e)) return;
    e.preventDefault();
    if (e.dataTransfer) e.dataTransfer.dropEffect = el.file.disabled ? 'none' : 'copy';
  });
  el.drop.addEventListener('drop', function (e) {
    if (!hasFiles(e)) return;
    e.preventDefault();
    dragDepth = 0; el.drop.classList.remove('over');
    if (el.file.disabled) return;
    var f = e.dataTransfer.files && e.dataTransfer.files[0];
    if (f) handleFile(f);
  });

  window.addEventListener('pagehide', revokeDownload);

  // ---------- boot ----------

  function boot() {
    show('pick');
    loadPromise = startRuntime();
  }

  // Dev only: ?mock swaps in mock-runtime.js (which overrides any real Silk43).
  if (/[?&]mock(=|&|$)/.test(location.search)) {
    var s = document.createElement('script');
    s.src = 'mock-runtime.js';
    s.onload = boot;
    s.onerror = function () { boot(); runtimeFailed('?mock was requested but mock-runtime.js could not be loaded.'); };
    document.head.appendChild(s);
  } else {
    boot();
  }
})();
