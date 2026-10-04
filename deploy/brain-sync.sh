#!/bin/sh
# AC-1364: the brain-sync sidecar's loop (deploy/compose.yaml). Runs as agent's uid, so what lands in the brain volume is
# the agent's; the app password is in this container alone. Never a full tree: each remote needs its include list.
set -u
export RCLONE_CONFIG=/run/secrets/cockpit_brain_rclone XDG_CACHE_HOME=/tmp/cache
interval=${BRAIN_SYNC_INTERVAL:-60}
# Space-separated `remote:path=local`, local relative to /data, whose first segment is the volume the pair lives on.
pairs=${BRAIN_SYNCS:-nc:Notes/AI-OS=Nextcloud/Notes/AI-OS}
idle=

say() { echo "brain-sync: $*"; }

sync_pair() {
  remote=${1%%=*} local=${1#*=} name=${1%%:*}
  filter=/etc/brain-sync/$name.filter
  # The bisync listings live on the pair's own volume, so a fresh volume is also a fresh state.
  workdir=/data/${local%%/*}/.bisync/$name
  [ -f "$filter" ] || { say "no include list $filter for $name, so it is not synced"; return 1; }
  mkdir -p "/data/$local" "$workdir"
  set -- bisync "$remote" "/data/$local" --workdir "$workdir" \
    --resilient --recover --max-lock 2m --conflict-resolve none --conflict-loser num
  # Only a pair without any listing gets a resync: a union copy that deletes nothing. After a failure, never.
  if [ -z "$(ls -A "$workdir")" ]; then
    say "$name has no bisync state yet: first run with --resync"
    set -- "$@" --resync
  fi
  if rclone "$@" --log-level NOTICE; then
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
  remotes=$(rclone listremotes 2>/dev/null)
  for pair in $pairs; do
    if ! printf '%s\n' "$remotes" | grep -qx "${pair%%:*}:"; then
      [ -n "$idle" ] || say "no remote ${pair%%:*} in the rclone config yet: idle"
      idle=1
      continue
    fi
    sync_pair "$pair"
    # ponytail: one halted pair halts them all; per-pair halting when a second brain is synced
    [ $? -ne 2 ] || exec sleep 2147483647
  done
  sleep "$interval"
done
