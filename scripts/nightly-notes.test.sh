#!/usr/bin/env bash
# Tests for scripts/nightly-notes.sh. Builds a throwaway git repository whose commits have the exact shapes
# main actually carries, runs the generator over them, and asserts on the notes it produces.
#
# Why a real repository rather than piping strings at the sed: the defects this guards against were not in
# the substitutions alone but in which branch the script took — a subject that no longer matched the
# bare-ticket test meant the commit body was never read at all. Only a real commit exercises that.
#
#   scripts/nightly-notes.test.sh
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
generator="${script_dir}/nightly-notes.sh"

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

cd "$work"
git init -q .
git config user.email test@example.invalid
git config user.name 'Notes Test'
git config commit.gpgsign false

commit() {
  # commit <subject> <body>
  printf 'change\n' >> file.txt
  git add file.txt
  git commit -q -m "$1" -m "$2"
}

base_message='seed'
printf 'seed\n' > file.txt
git add file.txt
git commit -q -m "$base_message"
base="$(git rev-parse HEAD)"

# Shape 1 — the squash merge appended the pull request number to a bare ticket subject. The description
# lives in the body bullets; before the fix the suffix broke the bare-ticket test and the body was skipped.
commit 'AC-331 (#205)' '- fixed: the redaction key did nothing when no region was marked out'

# Shape 2 — the subject carries the description, separated by an em dash rather than a hyphen.
commit 'AC-89 — a session'"'"'s MCP identity now dies with the session (#193)' ''

# Shape 3 — the convention without any squash suffix, which has to keep working.
commit 'AC-56' '- added: a plain bare-ticket subject still reads from its body'

# Shape 4 — a description mentioning a mid-sentence AC ref, and a word that opens with a full stop. The
# tidy-up that closes the gap left by the removed ref must not close the gap in front of ".NET".
commit 'AC-77' '- changed: the runner drops AC-12 from the text, and no .NET suite covers this script'

notes="$(bash "$generator" "${base}..HEAD")"

printf -- '--- generated notes ---\n%s\n-----------------------\n' "$notes"

assert_contains 'squash suffix: body bullet is used' \
  'fixed: the redaction key did nothing when no region was marked out' "$notes"

assert_absent 'squash suffix: bare pull request link is not the whole bullet' \
  '- (#205)' "$notes"

assert_contains 'em dash: description survives without a dangling dash' \
  "- a session's MCP identity now dies with the session (#193)" "$notes"

assert_absent 'em dash: bullet does not open with the separator' \
  '- — a' "$notes"

assert_contains 'plain bare ticket: body bullet is still used' \
  'added: a plain bare-ticket subject still reads from its body' "$notes"

assert_contains 'a word opening with a full stop keeps the space in front of it' \
  'no .NET suite covers this script' "$notes"

assert_absent 'the punctuation tidy-up does not glue a full stop to the word before it' \
  'no.NET' "$notes"

assert_contains 'a mid-sentence reference is removed and its gap closed' \
  'the runner drops from the text' "$notes"

assert_absent 'no internal tracker references leak into the notes' \
  'AC-' "$notes"

empty_notes="$(bash "$generator" 'HEAD..HEAD')"
assert_contains 'an empty range still says something truthful' \
  '_No code changes since the previous nightly._' "$empty_notes"

if [ "$failures" -ne 0 ]; then
  printf '\n%d assertion(s) failed\n' "$failures" >&2
  exit 1
fi

printf '\nall assertions passed\n'
