#!/usr/bin/env bash
# Regenerates UI/Runtime/cef-catalog.json for one CEF version: reads the file names and sizes from the official
# index, downloads every minimal archive, and pins our own SHA-256 (the index only carries a SHA-1, which is
# checked too). Usage: generate-cef-catalog.sh <cef-version, e.g. 154.0.34> [work-dir, must not exist]
set -euo pipefail

version="${1:?usage: generate-cef-catalog.sh <cef-version> [work-dir]}"
work="${2:-$(mktemp -d)}"
out="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/UI/Runtime/cef-catalog.json"
base="https://cef-builds.spotifycdn.com"

mkdir -p "$work"
[ -z "$(ls -A "$work")" ] || { echo "work dir $work is not empty" >&2; exit 1; }

curl -fsS "$base/index.json" -o "$work/index.json"

# <cockpit rid> <index platform>
pairs="win-x64:windows64 win-arm64:windowsarm64 osx-arm64:macosarm64 osx-x64:macosx64 linux-x64:linux64 linux-arm64:linuxarm64"
entries=""
separator=""
chromium=""
built=""
for pair in $pairs; do
  rid="${pair%%:*}"
  platform="${pair##*:}"
  # name, size, sha1, chromium version and build date of the minimal archive, tab separated
  row="$(python3 - "$work/index.json" "$platform" "$version" <<'PY'
import json, sys
index, platform, version = sys.argv[1:4]
for v in json.load(open(index))[platform]["versions"]:
    if v["cef_version"].startswith(version + "+"):
        f = next(f for f in v["files"] if f["type"] == "minimal")
        print("\t".join([f["name"], str(f["size"]), f["sha1"], v["chromium_version"], f["last_modified"][:10]]))
        break
else:
    sys.exit("CEF %s has no build for %s" % (version, platform))
PY
)"
  IFS=$'\t' read -r name size sha1 chromium built <<<"$row"
  curl -fsS -o "$work/$platform.tar.bz2" "$base/$(python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1]))' "$name")"
  [ "$(sha1sum "$work/$platform.tar.bz2" | cut -d' ' -f1)" = "$sha1" ] || { echo "SHA-1 of $name differs from the index" >&2; exit 1; }
  sha256="$(sha256sum "$work/$platform.tar.bz2" | cut -d' ' -f1)"
  url="$base/$(python3 -c 'import sys,urllib.parse;print(urllib.parse.quote(sys.argv[1]))' "$name")"
  entries="$entries$separator
    \"$rid\": { \"url\": \"$url\", \"size\": $size, \"sha256\": \"$sha256\" }"
  separator=","
done

cat > "$out" <<JSON
{
  "cefVersion": "$version",
  "chromiumVersion": "$chromium",
  "buildDate": "$built",
  "packages": {$entries
  }
}
JSON
echo "wrote $out"
