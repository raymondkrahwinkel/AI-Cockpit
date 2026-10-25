#!/usr/bin/env bash
# Verifies the per-RID zips of one plugin version against a store index (AC-1740). Static checks only, so it can
# only turn red on a real defect: publish-plugin.yml runs exactly this, and so can you, before anything is pushed.
#
#   scripts/verify-plugin-packages.sh <store dir> <plugin id> <version> <apphost path in the zip, no extension>
#   e.g. scripts/verify-plugin-packages.sh store browser 0.2.0 Subprocess/Cockpit.Plugin.Browser.Subprocess
#
# Per package in the index's `packages`:
#   * the zip hashes to the index's sha256, and has plugin.json at its root;
#   * the apphost's folder carries its own hostfxr (a self-contained publish, so no .NET is needed on the machine);
#   * win-*: the apphost .exe embeds the manifest with the Windows 10 supportedOS id (without it, CEF's offscreen
#     rendering freezes, AC-1709);
#   * everything else: the apphost has the exec bit inside the zip.
# Plus: the version's path/sha256 are the win-x64 package, as store-index-upsert.py writes them.
set -euo pipefail

if [ "$#" -ne 4 ]; then
  echo "usage: $0 <store dir> <plugin id> <version> <apphost path in the zip, no extension>" >&2
  exit 2
fi

store="$1"
plugin="$2"
version="$3"
apphost="$4"
apphost_dir="$(dirname "$apphost")"
supported_os_win10="8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a"

# jq on Windows ends its lines in CRLF; the same script has to read the same on a laptop as on the runner.
jqr() {
  jq -r "$@" | tr -d '\r'
}

entry="$(jq -c --arg id "$plugin" --arg v "$version" \
  '.plugins[]? | select(.id==$id) | .versions[]? | select(.version==$v)' "$store/index.json" | tr -d '\r')"
if [ -z "$entry" ]; then
  echo "error: $plugin $version is not in $store/index.json." >&2
  exit 1
fi

rids="$(printf '%s' "$entry" | jqr '.packages // {} | keys[]')"
if [ -z "$rids" ]; then
  echo "error: $plugin $version has no packages in the index." >&2
  exit 1
fi

failures=0
fail() {
  echo "error: $1" >&2
  failures=$((failures + 1))
}

for rid in $rids; do
  rel="$(printf '%s' "$entry" | jqr --arg rid "$rid" '.packages[$rid].path')"
  claimed="$(printf '%s' "$entry" | jqr --arg rid "$rid" '.packages[$rid].sha256')"
  zip="$store/$rel"

  if [ ! -f "$zip" ]; then
    fail "$rel ($rid) is in the index but not on disk."
    continue
  fi

  actual="$(sha256sum "$zip" | cut -d' ' -f1)"
  if [ "$actual" != "$claimed" ]; then
    fail "$rel ($rid) hashes to $actual; the index claims $claimed."
  fi

  listing="$(unzip -Z1 "$zip")"
  if ! printf '%s\n' "$listing" | grep -qx 'plugin.json'; then
    fail "$rel ($rid) has no plugin.json at its root."
  fi

  case "$rid" in
    win-*)
      if ! printf '%s\n' "$listing" | grep -qxF "$apphost_dir/hostfxr.dll"; then
        fail "$rel ($rid) has no $apphost_dir/hostfxr.dll: the subprocess is not self-contained."
      fi
      if ! printf '%s\n' "$listing" | grep -qxF "$apphost.exe"; then
        fail "$rel ($rid) has no $apphost.exe."
      elif ! unzip -p "$zip" "$apphost.exe" | LC_ALL=C grep -aqF "$supported_os_win10"; then
        fail "$rel ($rid): $apphost.exe carries no manifest with the Windows 10 supportedOS id; it was not built on Windows."
      fi
      ;;
    *)
      if ! printf '%s\n' "$listing" | grep -qxF "$apphost_dir/libhostfxr.so"; then
        fail "$rel ($rid) has no $apphost_dir/libhostfxr.so: the subprocess is not self-contained."
      fi
      mode="$(zipinfo "$zip" | awk -v path="$apphost" '$NF == path {print $1}')"
      if [ -z "$mode" ]; then
        fail "$rel ($rid) has no $apphost."
      elif [ "${mode:3:1}" != "x" ]; then
        fail "$rel ($rid): $apphost is $mode in the zip, without the exec bit."
      fi
      ;;
  esac
done

path="$(printf '%s' "$entry" | jqr '.path')"
sha="$(printf '%s' "$entry" | jqr '.sha256')"
if [ "$path|$sha" != "$(printf '%s' "$entry" | jqr '.packages["win-x64"] | "\(.path)|\(.sha256)"')" ]; then
  fail "$plugin $version: path/sha256 ($path) are not the win-x64 package."
fi

if [ "$failures" -gt 0 ]; then
  echo "$failures check(s) failed for $plugin $version." >&2
  exit 1
fi

echo "Verified $plugin $version: $(printf '%s\n' $rids | paste -sd' ') · hashes, self-contained, manifest and exec bit."
