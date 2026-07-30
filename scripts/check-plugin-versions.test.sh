#!/usr/bin/env bash
# Tests for scripts/check-plugin-versions.sh. Builds a throwaway repository holding the shapes a real pull
# request produces — a plugin whose code moved without its number, one that bumped properly, one where only
# the README changed, one where only its test project changed, a brand-new plugin, and a number that moved
# backwards — and runs the guard over the range.
#
# Why a real repository rather than feeding it paths: the guard's decision hinges on `git diff` between two
# refs and on reading plugin.json *at those refs*. A stubbed file list would exercise none of that, and the
# failure this protects against (a change that reaches nobody) is invisible in exactly that layer.
#
#   scripts/check-plugin-versions.test.sh
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
guard="${script_dir}/check-plugin-versions.sh"

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

# assert_contains <name> <needle> <haystack>
assert_contains() {
  case "$3" in
    *"$2"*) pass "$1" ;;
    *) fail "$1" "output containing: $2" "$3" ;;
  esac
}

# assert_absent <name> <needle> <haystack>
assert_absent() {
  case "$3" in
    *"$2"*) fail "$1" "output NOT containing: $2" "$3" ;;
    *) pass "$1" ;;
  esac
}

# assert_equals <name> <expected> <actual>
assert_equals() {
  if [ "$2" = "$3" ]; then pass "$1"; else fail "$1" "$2" "$3"; fi
}

# manifest <dir> <version>  — the fields the guard actually reads, in the order real manifests carry them.
# abstractionsVersion and minHostVersion are here on purpose: they are the near-misses the version match
# must not swallow.
manifest() {
  mkdir -p "$1"
  cat >"$1/plugin.json" <<JSON
{
  "id": "$(basename "$1" | tr '[:upper:]' '[:lower:]')",
  "version": "$2",
  "abstractionsVersion": 1,
  "minHostVersion": "0.9.0",
  "description": "A plugin."
}
JSON
}

cd "$work"
git init --quiet .
git config user.email test@example.com
git config user.name Test
git config commit.gpgsign false
# Without this, a Windows checkout floods the run with LF/CRLF warnings that bury the assertions.
git config core.autocrlf false

# --- base: five plugins, all released at 1.0.0 -------------------------------------------------------
for name in Alpha Beta Gamma Delta Epsilon; do
  manifest "plugins-dev/Cockpit.Plugin.${name}" 1.0.0
  printf 'int v = 1;\n' >"plugins-dev/Cockpit.Plugin.${name}/Plugin.cs"
  printf 'A plugin.\n' >"plugins-dev/Cockpit.Plugin.${name}/README.md"
done
mkdir -p plugins-dev/Cockpit.Plugin.Epsilon.Tests
printf 'int t = 1;\n' >plugins-dev/Cockpit.Plugin.Epsilon.Tests/Tests.cs
git add -A
git commit --quiet -m base
base="$(git rev-parse HEAD)"

# --- head: one shape per plugin ----------------------------------------------------------------------
printf 'int v = 2;\n' >plugins-dev/Cockpit.Plugin.Alpha/Plugin.cs            # code moved, number did not
printf 'int v = 2;\n' >plugins-dev/Cockpit.Plugin.Beta/Plugin.cs
manifest plugins-dev/Cockpit.Plugin.Beta 1.1.0                               # code moved, number bumped
printf 'Now with prose.\n' >plugins-dev/Cockpit.Plugin.Gamma/README.md       # markdown only
printf 'int v = 2;\n' >plugins-dev/Cockpit.Plugin.Delta/Plugin.cs
manifest plugins-dev/Cockpit.Plugin.Delta 0.9.0                              # number moved backwards
printf 'int t = 2;\n' >plugins-dev/Cockpit.Plugin.Epsilon.Tests/Tests.cs     # test project only
manifest plugins-dev/Cockpit.Plugin.Zeta 0.1.0                               # brand new plugin
printf 'int v = 1;\n' >plugins-dev/Cockpit.Plugin.Zeta/Plugin.cs
git add -A
git commit --quiet -m head

set +e
output="$("$guard" "$base" HEAD 2>&1)"
status=$?
set -e

assert_equals "a plugin that changed without a bump fails the run" 1 "$status"
assert_contains "the offending plugin is named" "Cockpit.Plugin.Alpha" "$output"
assert_contains "and the reason is that its number did not move" "unchanged" "$output"
assert_contains "a number that moved backwards is caught too" "Cockpit.Plugin.Delta" "$output"
assert_contains "and is reported as such, not as unchanged" "not newer" "$output"
assert_absent "a proper bump is not reported" "Cockpit.Plugin.Beta" "$output"
assert_absent "a README-only change needs no release" "Cockpit.Plugin.Gamma" "$output"
assert_absent "a test project ships nothing, so it needs no release" "Cockpit.Plugin.Epsilon" "$output"
assert_absent "a first release has nothing to bump past" "Cockpit.Plugin.Zeta" "$output"

# --- and the pass path, on the same content: fix both offenders --------------------------------------
manifest plugins-dev/Cockpit.Plugin.Alpha 1.0.1
manifest plugins-dev/Cockpit.Plugin.Delta 1.0.1
git add -A
git commit --quiet -m fixed

set +e
output="$("$guard" "$base" HEAD 2>&1)"
status=$?
set -e

assert_equals "once both are bumped the run is green" 0 "$status"
assert_contains "and says so" "ok:" "$output"

if [ "$failures" -ne 0 ]; then
  printf '\n%d test(s) failed\n' "$failures" >&2
  exit 1
fi

printf '\nall tests passed\n'
