#!/bin/sh
# AC-1364: the brain-sync sidecar's loop (deploy/compose.yaml). Runs as agent's uid, so what lands in the brain volume is
# the agent's; the app password is in this container alone. Never a full tree: each remote needs its include list.
set -u
export RCLONE_CONFIG=/run/secrets/cockpit_brain_rclone XDG_CACHE_HOME=/tmp/cache
interval=${BRAIN_SYNC_INTERVAL:-60}
# Space-separated `remote:path=local`, one per remote, local relative to /data, its first folder the pair's volume.
pairs=${BRAIN_SYNCS:-nc:Notes/AI-OS=Nextcloud/Notes/AI-OS}
idle= rpid=

say() { echo "brain-sync: $*"; }

# A stop interrupts bisync the way Ctrl-C does, its graceful shutdown, so no stale lock holds up the next start.
trap 'if [ -n "$rpid" ]; then kill -INT "$rpid" 2>/dev/null; wait "$rpid"; fi; exit 0' TERM INT

sync_pair() {
  remote=${1%%=*} local=${1#*=} name=${1%%:*}
  filter=/etc/brain-sync/$name.filter
  # The bisync listings live on the pair's own volume, so a fresh volume is also a fresh state.
  workdir=/data/${local%%/*}/.bisync/$name
  [ -f "$filter" ] || { say "no include list $filter for $name, so it is not synced"; return 1; }
  mkdir -p "/data/$local" "$workdir"
  set -- bisync "$remote" "/data/$local" --filter-from "$filter" --workdir "$workdir" \
    --resilient --recover --max-lock 2m --conflict-resolve none --conflict-loser num
  # A resync lets Nextcloud's version win wherever the two differ, so it runs only on a fresh volume: no state, no files.
  if [ -z "$(ls -A "$workdir")" ]; then
    if [ -n "$(ls -A "/data/$local")" ]; then
      say "$name stopped: files but no bisync state, so it needs a manual --resync (deploy/README.md)"
      return 2
    fi
    say "$name is a fresh volume: first run with --resync"
    set -- "$@" --resync
  fi
  rclone "$@" --log-level NOTICE & rpid=$!
  wait "$rpid"; code=$? rpid=
  if [ "$code" -eq 0 ]; then
    # What a past critical error left, so only a new one halts this sidecar.
    rm -f "$workdir"/*.lst-err
    say "$name in sync"
    return 0
  fi
  if ls "$workdir"/*.lst-err >/dev/null 2>&1; then
    say "$name stopped: bisync needs a manual --resync (deploy/README.md); this sidecar will not run one"
    return 2
  fi
  say "$name failed this cycle; bisync may retry it without --resync"
}

while :; do
  if [ -r "$RCLONE_CONFIG" ]; then remotes=$(rclone listremotes 2>/dev/null); else remotes=; fi
  for pair in $pairs; do
    if ! printf '%s\n' "$remotes" | grep -qx "${pair%%:*}:"; then
      [ -r "$RCLONE_CONFIG" ] || { say "cannot read $RCLONE_CONFIG: it must belong to uid 1700 (deploy/README.md)"; continue; }
      [ -n "$idle" ] || say "no remote ${pair%%:*} in the rclone config yet: idle"
      idle=1
      continue
    fi
    idle=
    sync_pair "$pair"
    # ponytail: one halted pair halts them all; per-pair halting when a second brain is synced
    [ $? -ne 2 ] || exec sleep 2147483647
  done
  sleep "$interval" & wait $!
done
