#!/bin/sh
# AC-1464: runs as app, after the entrypoint dropped root. Loads the session environment the entrypoint copied (compose
# env_file syntax: KEY=VALUE per line, `#` comments, no quoting) and starts the server, which the sessions inherit it from.
set -eu
env_file=/run/cockpit/session.env
if [ -f "$env_file" ]; then
  while IFS= read -r line || [ -n "$line" ]; do
    case $line in
      '' | '#'*) continue ;;
    esac
    name=${line%%=*}
    case $name in
      "$line" | '' | [0-9]* | *[!A-Za-z0-9_]*) echo "start-server: skipped a session.env line that is not KEY=VALUE" >&2; continue ;;
    esac
    export "$line"
  done < "$env_file"
fi
exec /app/Cockpit.Server "$@"
