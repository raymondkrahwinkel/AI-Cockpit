#!/bin/sh
# AC-1649: installed as /usr/local/libexec/cockpit-cli-promote. The server (app) downloads and verifies a managed CLI
# under /cli/.staging; this copies it into /cli/<name>/<version> as `agent`, open to agent-run alone, so the server can
# list the versions but never run one. /cli is root's, so neither user can swap the other's directory for a link.
# Fixed arguments, checked here: `promote <name> <version>` or `remove <name>`.
set -eu
self=/usr/local/libexec/cockpit-cli-promote
# One sudoers rule lets app run exactly this file as agent, with a reset environment (deploy/sudoers-agent).
if [ "$(/usr/bin/id -un)" != agent ]; then exec /usr/bin/sudo -n -u agent "$self" "$@"; fi
umask 027
fail() { echo "cockpit-cli-promote: $*" >&2; exit 64; }
action=${1:-} name=${2:-}
case $name in
  claude | codex) ;;
  *) fail "not a managed CLI: '$name'" ;;
esac
case $action in
  promote)
    [ $# -eq 3 ] || fail "usage: promote <name> <version>"
    version=$3
    # Digits and single dots only, as the server's Version.TryParse takes them: no path can climb out of /cli.
    case $version in '' | .* | *. | *..* | *[!0-9.]*) fail "not a version: '$version'" ;; esac
    staged=/cli/.staging/$name/$version
    [ -d "$staged" ] && [ ! -L "$staged" ] || fail "nothing staged for $name $version"
    copy=/cli/$name/.promote-$version
    /usr/bin/rm -rf "$copy"
    # A copy, not a move: the staging directory is the server's, and what lands here must be agent's.
    /usr/bin/cp -R "$staged" "$copy"
    /usr/bin/chgrp -R agent-run "$copy"
    /usr/bin/chmod -R u+rwX,g=rX,o= "$copy"
    /usr/bin/rm -rf "/cli/$name/$version"
    /usr/bin/mv "$copy" "/cli/$name/$version"
    # One version on disk, as on the desktop; a session still running an old one keeps its open binary.
    for old in "/cli/$name"/*; do
      [ "$old" = "/cli/$name/$version" ] || /usr/bin/rm -rf "$old"
    done
    ;;
  remove)
    [ $# -eq 2 ] || fail "usage: remove <name>"
    # /cli/<name> itself is the entrypoint's to make; only what is in it goes.
    /usr/bin/find "/cli/$name" -mindepth 1 -delete
    ;;
  *) fail "usage: promote <name> <version> | remove <name>" ;;
esac
