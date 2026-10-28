#!/usr/bin/env bash
# AC-1896 spike: the bridge bundle, reproducibly, from one pinned AI-Depot commit. Clean checkout, `npm ci` from Depot's lockfile, the
# same esbuild Depot builds with; then the hash check: the schema the bundle describes must hash to the version of that commit's
# schema.json (what Join sends). Prints the bundles' SHA-256, so two builds of the same commit can be compared.
# Usage: build-pinned.sh <commit> <work dir>
set -euo pipefail
commit="$1"; work="$2"; here="$(cd "$(dirname "$0")" && pwd)"
rm -rf "$work/depot"; mkdir -p "$work"
git clone -q --filter=blob:none --no-checkout https://github.com/raymondkrahwinkel/AI-Depot "$work/depot"
git -C "$work/depot" checkout -q "$commit"
notes="$work/depot/src/Depot.Api/ClientApp/notes"
(cd "$notes" && npm ci --no-audit --no-fund --ignore-scripts >/dev/null 2>&1 && npm rebuild esbuild >/dev/null 2>&1)
node "$here/build.mjs" "$notes" "$work/notes.jint.js" >/dev/null
node "$here/build.mjs" "$notes" "$work/bridge.jint.js" --yjs >/dev/null
fixtures="$work/depot/scripts/notes-roundtrip-fixtures"
expected="$(tr -d '\r' < "$fixtures/schema.json" | sha256sum | cut -c1-12)"
actual="$(node -e "const vm=require('vm'),c={};vm.createContext(c);vm.runInContext(require('fs').readFileSync(process.argv[1],'utf8')+';globalThis.N=DepotNotes',c);process.stdout.write(require('crypto').createHash('sha256').update(c.N.schemaJson()).digest('hex').slice(0,12))" "$work/notes.jint.js")"
echo "commit $commit"
echo "schema.json version $expected, bundle schema version $actual"
(cd "$work" && sha256sum notes.jint.js bridge.jint.js)
[ "$expected" = "$actual" ] || { echo "FAIL the bundle's schema is not the commit's schema.json"; exit 1; }
echo "PASS"
