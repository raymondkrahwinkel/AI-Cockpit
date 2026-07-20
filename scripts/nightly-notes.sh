#!/usr/bin/env bash
# Readable, tracker-number-free notes for the rolling nightly (AC-127). The nightly's body is "what landed
# since the previous nightly", built from the commit range. A raw `git log --pretty='- %s'` is not enough:
# our commit convention (see CONTRIBUTING) puts only the ticket id on the subject line (e.g. "AC-56") and
# the description in the body bullets, so a subject-only log shows a list of bare ticket numbers that mean
# nothing to a downloader. This prefers the body bullets, falls back to the subject for commits that carry
# their description there (descriptive or conventional-commit subjects), and strips internal AC-#### refs.
#
#   scripts/nightly-notes.sh <git-log-range-args>      # e.g. "abc123..HEAD"  or  -n 20
set -euo pipefail

# Strip internal tracker references from one line and tidy up what their removal leaves behind: a leading
# "AC-123 - " / "AC-123: " prefix (with or without a bullet), a parenthesised "(AC-123 …)" ref, and any
# remaining bare "AC-123"; then drop emptied parentheses, collapse doubled spaces, pull spaces back off
# punctuation, and trim the ends.
strip_tickets() {
  sed -E \
    -e 's/^([[:space:]]*-[[:space:]]+)?AC-[0-9]+[[:space:]]*[-:][[:space:]]*/\1/' \
    -e 's/[[:space:]]*\(AC-[0-9]+[^)]*\)//g' \
    -e 's/\bAC-[0-9]+\b//g' \
    -e 's/\([[:space:]]*\)//g' \
    -e 's/[[:space:]]{2,}/ /g' \
    -e 's/[[:space:]]+([.,;:)])/\1/g' \
    -e 's/^[[:space:]]+//' \
    -e 's/[[:space:]]+$//'
}

any=0
while IFS= read -r sha; do
  [ -n "$sha" ] || continue
  subject="$(git log -1 --pretty=format:'%s' "$sha")"
  body="$(git log -1 --pretty=format:'%b' "$sha")"

  if printf '%s' "$subject" | grep -qE '^AC-[0-9]+$' && printf '%s\n' "$body" | grep -qE '^[[:space:]]*-[[:space:]]'; then
    # Bare-ticket subject with a bulleted body: the description lives in the bullets — emit those.
    printf '%s\n' "$body" | grep -E '^[[:space:]]*-[[:space:]]' | while IFS= read -r line; do
      printf '%s\n' "$line" | strip_tickets
    done
  else
    # The subject carries the description (descriptive "AC-34 - refined: …" or conventional "fix(x): …").
    # Drop any leading bullet the subject already starts with so it is not doubled below.
    cleaned="$(printf '%s' "$subject" | strip_tickets | sed -E 's/^-[[:space:]]+//')"
    [ -n "$cleaned" ] && printf -- '- %s\n' "$cleaned"
  fi
  any=1
done < <(git log --no-merges --reverse --pretty=format:'%H' "$@")

# A range with no non-merge commits should still leave a truthful, non-empty note.
[ "$any" -eq 1 ] || echo "_No code changes since the previous nightly._"
