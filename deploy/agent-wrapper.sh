#!/bin/sh
# AC-1464: installed as /opt/cockpit/bin/claude, codex and git, first on PATH, so every start of a CLI and every git
# (whose hooks and config an agent can plant in a shared repository) runs as `agent`. -E keeps the session's environment
# (COCKPIT_PANE_ID, Codex's MCP bearer variables, GH_TOKEN); HOME is the agent's. Absolute paths: PATH is the session's.
name=${0##*/}
case $name in
  git) real=/usr/bin/git ;;
  # AC-1649: claude and codex go through cockpit-cli-exec, which picks a managed version under /cli or the image's own.
  *) set -- "$name" "$@"; real=/usr/local/libexec/cockpit-cli-exec ;;
esac
# A CLI already running as agent that calls git or claude itself goes straight through.
if [ "$(/usr/bin/id -un)" = agent ]; then exec "$real" "$@"; fi
path=$(printf '%s' "$PATH" | /usr/bin/sed -e 's#/opt/cockpit/bin:##g' -e 's#:/opt/cockpit/bin$##')
exec /usr/bin/sudo -n -E -u agent HOME=/home/agent PATH="$path" "$real" "$@"
