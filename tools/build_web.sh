#!/usr/bin/env bash
# builds the browser patcher into web/dist/ (a static site). run from the repo root:
#   tools/build_web.sh
set -euo pipefail

cd "$(dirname "$0")/.."

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "[+] publishing web/wasm (interpreter, trimmed, no workloads)"
dotnet publish web/wasm/PatcherWasm.csproj -c Release -o "$TMP/pub" --nologo -v:minimal

FRAMEWORK="$TMP/pub/wwwroot/_framework"
if [ ! -d "$FRAMEWORK" ]; then
  echo "[!] _framework not found in the publish output" >&2
  exit 1
fi

DIST=web/dist
rm -rf "$DIST"
mkdir -p "$DIST/_framework"

# the host (GitHub Pages, etc.) compresses on the fly; the precompressed .gz/.br copies are never requested
find "$FRAMEWORK" -maxdepth 1 -type f ! -name '*.gz' ! -name '*.br' -exec cp {} "$DIST/_framework/" \;

for f in index.html style.css main.js patcher-runtime.js patcher-worker.js; do
  cp "web/app/$f" "$DIST/$f"
done

# GitHub Pages runs Jekyll by default, which drops directories that start with an underscore
touch "$DIST/.nojekyll"

echo "[+] web/dist ready"
echo "    _framework: $(du -sk "$DIST/_framework" | cut -f1) KB raw, $(tar -cf - -C "$DIST" _framework | gzip -9 | wc -c | awk '{printf "%d KB", $1/1024}') gzip"
echo "    total:      $(du -sk "$DIST" | cut -f1) KB"
