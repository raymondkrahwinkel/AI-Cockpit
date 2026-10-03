#!/bin/sh
# AC-1464: installed as /opt/cockpit/bin/claude and /opt/cockpit/bin/codex, first on PATH, so every start of either CLI
# (sessions, sign-ins, login checks) runs as `agent`. -E keeps the session's environment (COCKPIT_PANE_ID, Codex's MCP
# bearer variables, GH_TOKEN), which a bare sudo would reset; HOME is the agent's, and PATH no longer leads back here.
exec sudo -n -E -u agent HOME=/home/agent PATH="${PATH#/opt/cockpit/bin:}" "/usr/local/bin/${0##*/}" "$@"
