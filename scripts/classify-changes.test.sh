#!/usr/bin/env bash
# Tests for scripts/classify-changes.sh. Builds a throwaway repository, then re-bases the same starting
# point onto one commit per scenario (docs-only, plugins-dev-only, src, mixed, ambiguous) and checks the
# three flags the guard prints. Every downstream `if:` in ci.yml hinges on these flags, so a wrong flag
# here means a job silently skips (a missed regression) or silently always runs (defeats the ticket).
#
#   scripts/classify-changes.test.sh
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
guard="${script_dir}/classify-changes.sh"

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

assert_equals() {
  if [ "$2" = "$3" ]; then pass "$1"; else fail "$1" "$2" "$3"; fi
}

cd "$work"
git init --quiet .
git config user.email test@example.com
git config user.name Test
git config commit.gpgsign false
git config core.autocrlf false

mkdir -p src/Cockpit.Core plugins-dev/Cockpit.Plugin.Foo docs scripts .github/workflows
printf 'seed\n' >src/Cockpit.Core/Seed.cs
printf 'seed\n' >plugins-dev/Cockpit.Plugin.Foo/Seed.cs
printf '# seed\n' >docs/seed.md
printf 'seed\n' >README.md
printf 'seed\n' >CHANGELOG.md
printf 'seed\n' >scripts/seed.sh
git add -A
git commit --quiet -m base
base="$(git rev-parse HEAD)"

# run <name> <expected src> <expected plugins_dev> <expected other> <path>...
run() {
  local name="$1" want_src="$2" want_plugins_dev="$3" want_other="$4"
  shift 4
  git checkout --quiet "$base"
  for path in "$@"; do printf 'changed\n' >>"$path"; done
  git add -A
  git commit --quiet -m "$name"

  local out src plugins_dev other
  out="$("$guard" "$base" HEAD)"
  src="$(printf '%s\n' "$out" | grep '^src=' | cut -d= -f2)"
  plugins_dev="$(printf '%s\n' "$out" | grep '^plugins_dev=' | cut -d= -f2)"
  other="$(printf '%s\n' "$out" | grep '^other=' | cut -d= -f2)"

  assert_equals "$name: src" "$want_src" "$src"
  assert_equals "$name: plugins_dev" "$want_plugins_dev" "$plugins_dev"
  assert_equals "$name: other" "$want_other" "$other"
}

run "docs-only (md)"          false false false README.md
run "docs-only (docs/)"       false false false docs/seed.md
run "docs-only (changelog)"   false false false CHANGELOG.md
run "plugins-dev-only"        false true  false plugins-dev/Cockpit.Plugin.Foo/Seed.cs
run "src change"              true  false false src/Cockpit.Core/Seed.cs
run "src plus plugins-dev"    true  true  false src/Cockpit.Core/Seed.cs plugins-dev/Cockpit.Plugin.Foo/Seed.cs
run "ambiguous script"        false false true  scripts/seed.sh
run "ambiguous workflow file" false false true  .github/workflows/ci.yml

if [ "$failures" -ne 0 ]; then
  printf '\n%d test(s) failed\n' "$failures" >&2
  exit 1
fi

printf '\nall tests passed\n'
