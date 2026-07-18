#!/usr/bin/env bash
# Changelog plumbing for the Release workflow (AC-73 follow-up: a changelog kept from one release to
# the next). Two jobs, both pure text over CHANGELOG.md so they can be run and tested by hand:
#
#   scripts/changelog.sh notes                     # print the cleaned [Unreleased] section
#   scripts/changelog.sh roll 1.2.3 2026-07-18      # move [Unreleased] under a dated [1.2.3], in place
#
# "notes" is the text the GitHub release body is built from. "roll" is the bookkeeping the release does
# after the tag is cut: the accumulated entries move under the version that was just released, and a
# fresh empty [Unreleased] is opened for the next one. The version is the tag — never written by hand.
set -euo pipefail

FILE="${CHANGELOG_FILE:-CHANGELOG.md}"

# Everything between the [Unreleased] heading and the next version heading, with the empty "### Section"
# headers dropped (a header with no bullet under it is noise in a release note), HTML comments stripped,
# and the blank lines squeezed. A section that never got an entry simply does not appear.
notes() {
  awk '
    /^## \[Unreleased\]/ { grab=1; next }
    /^## \[/             { grab=0 }
    !grab                { next }
    /<!--/               { inc=1 }
    inc                  { if (/-->/) inc=0; next }
    /^### /              { hdr=$0; next }        # hold a section header until content follows it
    /^[[:space:]]*$/     { next }                # drop blank lines; re-inserted before each kept header
    {
      if (hdr != "") { buf[n++]=""; buf[n++]=hdr; hdr="" }
      buf[n++]=$0
    }
    END {
      s=0; while (s < n && buf[s]=="") s++;       # trim the leading blank
      for (i=s; i<n; i++) print buf[i]
    }
  ' "$FILE"
}

# Rename [Unreleased] to [VERSION] - DATE and open a fresh, empty [Unreleased] above it. The entries
# already there stay exactly where they are — they just end up under the version heading.
roll() {
  local version="${1:?usage: changelog.sh roll VERSION DATE}"
  local date="${2:?usage: changelog.sh roll VERSION DATE}"
  local tmp
  tmp="$(mktemp)"
  awk -v v="$version" -v d="$date" '
    /^## \[Unreleased\]/ {
      print "## [Unreleased]"
      print ""
      print "### Added"
      print ""
      print "### Changed"
      print ""
      print "### Fixed"
      print ""
      print "### Removed"
      print ""
      print "## [" v "] - " d
      next
    }
    { print }
  ' "$FILE" > "$tmp"
  mv "$tmp" "$FILE"
}

case "${1:-}" in
  notes) notes ;;
  roll)  roll "${2:-}" "${3:-}" ;;
  *)     echo "usage: changelog.sh {notes | roll VERSION DATE}" >&2; exit 2 ;;
esac
