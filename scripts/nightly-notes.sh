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

# A squash merge appends the pull request number to the subject: "AC-331" becomes "AC-331 (#205)". That
# suffix is GitHub's, not ours, and it has to come off before the subject is recognised as a bare ticket id
# — otherwise the bare-ticket test below never matches and the body bullets holding the actual description
# are never read.
drop_pr_suffix() {
  sed -E 's/[[:space:]]*\(#[0-9]+\)[[:space:]]*$//'
}

# Strip internal tracker references from one line and tidy up what their removal leaves behind: a leading
# "AC-123 - " / "AC-123: " prefix (with or without a bullet), a parenthesised "(AC-123 …)" ref, and any
# remaining bare "AC-123"; then drop emptied parentheses, collapse doubled spaces, pull spaces back off
# punctuation, and trim the ends.
#
# The punctuation tidy-up only fires when the mark ends a word — it is there to close the gap left by a
# removed mid-sentence reference ("see AC-123 ." becomes "see ."). Without that guard it also closes the
# gap in front of a word that opens with one, turning "no .NET suite" into "no.NET suite".
#
# The separator after the ticket id is an en/em dash as often as a hyphen, because that is what the subjects
# on main actually use. Matching only "-" left the dash behind and produced bullets that opened with a
# dangling "— ".
strip_tickets() {
  sed -E \
    -e 's/^([[:space:]]*-[[:space:]]+)?AC-[0-9]+[[:space:]]*(-|:|—|–)[[:space:]]*/\1/' \
    -e 's/[[:space:]]*\(AC-[0-9]+[^)]*\)//g' \
    -e 's/\bAC-[0-9]+\b//g' \
    -e 's/\([[:space:]]*\)//g' \
    -e 's/[[:space:]]{2,}/ /g' \
    -e 's/[[:space:]]+([.,;:)])([[:space:]]|$)/\1\2/g' \
    -e 's/^[[:space:]]+//' \
    -e 's/[[:space:]]+$//'
}

any=0
while IFS= read -r sha; do
  [ -n "$sha" ] || continue
  subject="$(git log -1 --pretty=format:'%s' "$sha")"
  body="$(git log -1 --pretty=format:'%b' "$sha")"

  subject_core="$(printf '%s' "$subject" | drop_pr_suffix)"

  if printf '%s' "$subject_core" | grep -qE '^AC-[0-9]+$' && printf '%s\n' "$body" | grep -qE '^[[:space:]]*-[[:space:]]'; then
    # Bare-ticket subject with a bulleted body: the description lives in the bullets — emit those.
    printf '%s\n' "$body" | grep -E '^[[:space:]]*-[[:space:]]' | while IFS= read -r line; do
      printf '%s\n' "$line" | strip_tickets
    done
  else
    # The subject carries the description (descriptive "AC-34 - refined: …" or conventional "fix(x): …").
    # Drop any leading bullet the subject already starts with so it is not doubled below.
    #
    # A bare ticket id whose commit has no bulleted body also lands here and leaves only the "(#205)" link.
    # That is kept on purpose: the change has no human-readable description anywhere, and a clickable link
    # to the pull request is both the most that can honestly be said and a visible sign that the commit
    # message needs fixing. Dropping the line would make the notes silently incomplete instead.
    cleaned="$(printf '%s' "$subject" | strip_tickets | sed -E 's/^-[[:space:]]+//')"
    [ -n "$cleaned" ] && printf -- '- %s\n' "$cleaned"
  fi
  any=1
    # `tformat:` terminates every record with a newline; `format:` only separates them, leaving the last
    # commit on an unterminated line. `read` then assigns it but reports end-of-input, so the loop ends
    # before the body runs and the newest commit in the range is dropped from the notes without a trace.
done < <(git log --no-merges --reverse --pretty=tformat:'%H' "$@")

# A range with no non-merge commits should still leave a truthful, non-empty note.
[ "$any" -eq 1 ] || echo "_No code changes since the previous nightly._"
