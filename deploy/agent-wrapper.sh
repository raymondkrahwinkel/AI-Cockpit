#!/bin/sh
# AC-1464: installed as /opt/cockpit/bin/claude, codex and git, first on PATH, so every start of a CLI and every git
# (whose hooks and config an agent can plant in a shared repo) runs as `agent`. -E keeps the session's environment
# (COCKPIT_PANE_ID, Codex's MCP bearer variables, GH_TOKEN), which a bare sudo would reset; HOME is the agent's.
name=${0##*/}
case $name in
  git) real=/usr/bin/git ;;
  *) real=/usr/local/bin/$name ;;
esac
# A CLI already running as agent that calls git or claude itself goes straight through.
if [ "$(id -un)" = agent ]; then exec "$real" "$@"; fi
exec sudo -n -E -u agent HOME=/home/agent PATH="$(printf '%s' "$PATH" | sed -e 's#/opt/cockpit/bin:##g' -e 's#:/opt/cockpit/bin$##')" "$real" "$@"
