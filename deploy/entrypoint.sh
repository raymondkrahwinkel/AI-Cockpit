#!/bin/sh
# AC-1464: starts as root only to hand the server its secrets, then drops to `app` for good. Agent sessions run as
# `agent` through the claude and codex wrappers on PATH, so they cannot read these copies, /state or the server's /proc.
set -eu

# Compose file secrets outside swarm ignore uid and mode, and anyone in the container could read them. Each secret the
# server is pointed at is copied to a tmpfs only `app` can enter, and the originals' directory is closed to everyone
# but root. An empty variable is passed on as it is: the server itself says which one it misses.
copies=/run/cockpit
mkdir -p "$copies"
chown app:app "$copies"
chmod 0700 "$copies"
mountpoint -q "$copies" || echo "entrypoint: $copies is not a tmpfs; the secret copies live in the container's own layer" >&2
for var in COCKPIT_UNLOCK_PASSWORD_FILE COCKPIT_CONNECT_KEY_FILE; do
  eval "source=\${$var:-}"
  if [ -n "$source" ] && [ -f "$source" ]; then
    install -o app -g app -m 0400 "$source" "$copies/${source##*/}"
    export "$var=$copies/${source##*/}"
  fi
done
# The session environment is a file too, loaded only after the drop (start-server.sh): as a compose env_file it was the
# container's own environment, which `docker exec` and the health check run with as root.
if [ -f /run/secrets/cockpit_session_env ]; then
  install -o app -g app -m 0400 /run/secrets/cockpit_session_env "$copies/session.env"
fi
if [ -d /run/secrets ]; then chmod 0700 /run/secrets; fi

# A managed CLI install runs as app, past the wrapper. /state/cli is root's and never empty, so the server can neither
# install one nor find one there and falls back to PATH; an install from before AC-1464 is moved aside.
if [ -e /state/cli ] && [ "$(stat -c %U /state/cli)" != root ]; then mv /state/cli "/state/cli.disabled-$(date +%s)"; fi
mkdir -p /state/cli
chown root:root /state/cli
chmod 0755 /state/cli
touch /state/cli/.root-owned

# Volumes from before AC-1464 belong to `app`; the CLIs that write them now run as `agent`.
for home in /home/agent/.claude /home/agent/.codex; do
  if [ "$(stat -c %U "$home")" != agent ]; then chown -R agent:agent "$home"; fi
  chmod 2770 "$home"
done

# The group the Claude provider opens its mcp-config and prompt file to (AC-1468); `app` is in it, `agent` owns it.
COCKPIT_AGENT_GROUP=$(id -g agent)
export COCKPIT_AGENT_GROUP
# Group-writable, so a worktree under /work stays writable for both users; nothing for anyone else.
umask 007
exec /usr/bin/setpriv --reuid=app --regid=app --init-groups -- /opt/cockpit/start-server.sh "$@"
