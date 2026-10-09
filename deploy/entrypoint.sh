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
for var in COCKPIT_CONNECT_KEY_FILE; do
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

# Volumes from before AC-1464 belong to `app`; the CLIs that write them now run as `agent`. The brain instructions
# mounted read-only inside them (AC-1364) are skipped: a chown there fails and would stop this script.
for home in /home/agent/.claude /home/agent/.codex; do
  if [ "$(stat -c %U "$home")" != agent ]; then
    find "$home" \( -path "$home/CLAUDE.md" -o -path "$home/AGENTS.md" \) -prune -o -exec chown -h agent:agent {} +
  fi
  chmod 2770 "$home"
done

# AC-1480: a bind mount keeps its host owner and mode. Per path: the right owner and writable, or the start stops with
# the fix; a mode wider than meant (group or other could read /state) is narrowed here, with a notice.
bad=0
check_dir() {
  user=$1 owner=$2 mode=$3 path=$4
  if ! /usr/bin/setpriv --reuid="$user" --regid="$user" --init-groups -- test -w "$path"; then
    echo "entrypoint: $path is not writable by $user (uid $(id -u "$user")): not mounted read-only? Else fix the host path's owner (README.md, Persistent data)" >&2
    bad=1
  elif [ "$(stat -c %U "$path")" != "$owner" ]; then
    echo "entrypoint: $path is owned by $(stat -c %U "$path"), not $owner (uid $(id -u "$owner")): chown the host path to $(id -u "$owner") (README.md, Persistent data)" >&2
    bad=1
  elif [ "$(stat -c %a "$path")" != "$mode" ]; then
    echo "entrypoint: $path had mode $(stat -c %a "$path"), now $mode" >&2
    chmod "$mode" "$path"
  fi
}
check_dir app app 700 /state
check_dir app app 2770 /work
/usr/bin/setpriv --reuid=agent --regid=agent --init-groups -- test -w /work || { echo "entrypoint: /work is not writable by agent (uid $(id -u agent)): chown the host path to $(id -u app):$(id -g agent), mode 2770 (README.md, Persistent data)" >&2; bad=1; }
check_dir app app 700 /home/app/.ssh
# The server's side of the logins is agent's, written through the group.
for path in /home/agent/.claude /home/agent/.codex; do check_dir agent agent 2770 "$path"; done
for path in /home/app/.claude /home/app/.codex; do check_dir app agent 2770 "$path"; done
check_dir agent agent 700 /home/agent/.ssh
check_dir agent agent 700 /home/agent/Nextcloud
[ "$bad" = 0 ] || exit 1

# The group the Claude provider opens its mcp-config and prompt file to (AC-1468); `app` is in it, `agent` owns it.
COCKPIT_AGENT_GROUP=$(id -g agent)
export COCKPIT_AGENT_GROUP
# Group-writable, so a worktree under /work stays writable for both users; nothing for anyone else.
umask 007
exec /usr/bin/setpriv --reuid=app --regid=app --init-groups -- /opt/cockpit/start-server.sh "$@"
