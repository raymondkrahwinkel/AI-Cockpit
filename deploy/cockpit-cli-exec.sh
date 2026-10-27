#!/bin/sh
# AC-1649: installed as /usr/local/libexec/cockpit-cli-exec, root's and agent-run's alone, and started by the claude
# and codex wrappers as `agent`. The newest managed version under /cli wins over the image's own copy; with none (or
# after a Remove) the image's copy runs. Absolute paths: PATH is the session's.
name=${1:-}
case $name in
  claude | codex) ;;
  *) echo "cockpit-cli-exec: not a managed CLI: '$name'" >&2; exit 64 ;;
esac
shift
newest=$(/usr/bin/ls -1 "/cli/$name" 2>/dev/null | /usr/bin/grep -Ex '[0-9]+(\.[0-9]+){1,3}' | /usr/bin/sort -V | /usr/bin/tail -n 1)
if [ -n "$newest" ] && [ -x "/cli/$name/$newest/$name" ]; then exec "/cli/$name/$newest/$name" "$@"; fi
exec "/usr/local/bin/$name" "$@"
