#!/usr/bin/env bash
# Tests for scripts/check-plugin-abstractions-version.sh. Builds a throwaway checkout of the real repo layout
# — the guard reads AbstractionsContract.cs and every plugins-dev/Cockpit.Plugin.*/plugin.json relative to its
# own location, so it has to run against real files, not stubbed paths.
#
#   scripts/check-plugin-abstractions-version.test.sh
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
guard="${script_dir}/check-plugin-abstractions-version.sh"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

failures=0

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  printf '  expected: %s\n' "$2" >&2
  printf '  actual:   %s\n' "$3" >&2
  failures=$((failures + 1))
}

pass() {
  printf 'ok: %s\n' "$1"
}

assert_contains() {
  case "$3" in
    *"$2"*) pass "$1" ;;
    *) fail "$1" "output containing: $2" "$3" ;;
  esac
}

assert_absent() {
  case "$3" in
    *"$2"*) fail "$1" "output NOT containing: $2" "$3" ;;
    *) pass "$1" ;;
  esac
}

assert_equals() {
  if [ "$2" = "$3" ]; then pass "$1"; else fail "$1" "$2" "$3"; fi
}

# manifest <dir> <abstractionsVersion> — "version" and "minHostVersion" are here too, the near-misses the
# match must not trip on.
manifest() {
  mkdir -p "$1"
  cat >"$1/plugin.json" <<JSON
{
  "id": "$(basename "$1" | tr '[:upper:]' '[:lower:]')",
  "version": "1.0.0",
  "abstractionsVersion": $2,
  "minHostVersion": "0.9.0",
  "description": "A plugin."
}
JSON
}

mkdir -p "${work}/scripts" "${work}/src/Cockpit.Plugins.Abstractions" "${work}/plugins-dev"
cp "$guard" "${work}/scripts/check-plugin-abstractions-version.sh"
cat >"${work}/src/Cockpit.Plugins.Abstractions/AbstractionsContract.cs" <<'CS'
namespace Cockpit.Plugins.Abstractions;

public static class AbstractionsContract
{
    public const int Version = 2;
}
CS

manifest "${work}/plugins-dev/Cockpit.Plugin.Alpha" 2   # matches
manifest "${work}/plugins-dev/Cockpit.Plugin.Beta" 1    # stale, the AC-1039 shape
mkdir -p "${work}/plugins-dev/Cockpit.Plugin.Beta.Tests"
cat >"${work}/plugins-dev/Cockpit.Plugin.Beta.Tests/plugin.json" <<'JSON'
{ "abstractionsVersion": 1 }
JSON

cd "$work"
set +e
output="$(bash scripts/check-plugin-abstractions-version.sh 2>&1)"
status=$?
set -e

assert_equals "a mismatched plugin fails the run" 1 "$status"
assert_contains "names the offending plugin" "Cockpit.Plugin.Beta" "$output"
assert_contains "states its declared value" "abstractionsVersion: 1" "$output"
assert_contains "and the expected value" "expected: 2" "$output"
assert_absent "a matching plugin is not reported" "Cockpit.Plugin.Alpha" "$output"
assert_absent "a .Tests project is not a plugin and is skipped" "Cockpit.Plugin.Beta.Tests" "$output"

# --- and the pass path, on the same content: fix the offender -----------------------------------------
manifest "${work}/plugins-dev/Cockpit.Plugin.Beta" 2

set +e
output="$(bash scripts/check-plugin-abstractions-version.sh 2>&1)"
status=$?
set -e

assert_equals "once fixed the run is green" 0 "$status"
assert_contains "and says so" "ok:" "$output"

if [ "$failures" -ne 0 ]; then
  printf '\n%d test(s) failed\n' "$failures" >&2
  exit 1
fi

printf '\nall tests passed\n'
