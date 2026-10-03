#!/usr/bin/env bash
# AC-1356: the container smoke, run by CI before the push. Usage: deploy/smoke.sh <image:tag> (from the repo root).
# Seeds a state, then proves: a start with both secrets answers on the node door; a restart keeps the fingerprint and a
# fresh volume does not; no unlock secret means a refusal; the server runs non-root with no docker and no Avalonia; an
# agent session runs as `agent` with the session's env and cannot read the state, the secrets or the server (AC-1464);
# no secret is in a layer or a log. Each check that could pass vacuously has a control that must come out red.
set -euo pipefail

image=${1:?usage: deploy/smoke.sh <image:tag>}
export COCKPIT_IMAGE=${image%:*} COCKPIT_TAG=${image##*:}
project=cockpit-smoke
work=$(mktemp -d)
mkdir -p "$work/secrets" "$work/seed"
export COCKPIT_UNLOCK_PASSWORD_PATH=$work/secrets/unlock-password COCKPIT_CONNECT_KEY_PATH=$work/secrets/connect-key
unlock=$(openssl rand -hex 16)
key="ck_$(openssl rand -base64 48 | tr '+/' '-_' | tr -d '=\n')"
printf '%s' "$unlock" > "$COCKPIT_UNLOCK_PASSWORD_PATH"
printf '%s' "$key" > "$COCKPIT_CONNECT_KEY_PATH"
# Owner-only on the host: the entrypoint reads them as root and hands the server its own copies (AC-1464).
chmod 600 "$COCKPIT_UNLOCK_PASSWORD_PATH" "$COCKPIT_CONNECT_KEY_PATH"
: > "$work/logs.txt"

dc() { docker compose -p "$project" -f deploy/compose.yaml "$@"; }
fail() { echo "::error::smoke: $*"; exit 1; }
cleanup() {
  dc down -v --remove-orphans >/dev/null 2>&1 || true
  docker rm -f "$project-holder" "$project-control-root" >/dev/null 2>&1 || true
  docker rmi -f "$project-control-leak" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

# The uid a container's server process runs as: the image starts as root and the entrypoint drops (AC-1464).
server_uid() { docker top "$1" -eo uid,args | awk '$2 == "/app/Cockpit.Server" { print $1 }'; }
# One field of a probe's JSON line (deploy/smoke-probe.js).
field() { node -e 'process.stdout.write(String(JSON.parse(process.argv[1])[process.argv[2]]))' "$1" "$2"; }
# The probe, loaded into the codex launcher of whatever `docker compose exec` starts here, as the server's user.
# Under the server's umask (007, deploy/entrypoint.sh), which `exec` does not inherit. Usage: probe <cli> [exec options].
probe() {
  local cli=$1; shift
  dc exec -T -u app -e COCKPIT_PANE_ID=smoke-pane -e NODE_OPTIONS=--require=/work/smoke-probe.js "$@" cockpit     sh -c "umask 007 && exec $cli --version" | sed -n 's/^PROBE //p'
}
# Into a variable first: `grep -q` ends the pipe early, which `pipefail` reads as a failure.
history_leaks() { local layers; layers=$(docker history --no-trunc --format '{{.CreatedBy}}' "$1"); grep -qF "$2" <<< "$layers"; }
collect_logs() { dc logs --no-color cockpit >> "$work/logs.txt" 2>&1 || true; }
log_has() { local out; out=$(dc logs --no-color cockpit); grep -qF "$1" <<< "$out"; }
fingerprint_of_log() { dc logs --no-color cockpit | sed -n 's/.*presents certificate fingerprint \([0-9A-Fa-f]*\)\..*/\1/p' | tail -n 1; }
# Bounded at 60 s; ends early when the container is gone.
wait_running() {
  for _ in $(seq 60); do
    log_has 'Cockpit.Server running' && return 0
    [ -n "$(dc ps -q --status running cockpit)" ] || break
    sleep 1
  done
  dc logs --no-color cockpit | sed -e "s/$unlock/<unlock>/g" -e "s/$key/<key>/g" | tail -n 30
  fail "the server did not report running"
}
# The volume is created by compose; `seed` copies the encryption-on state in, the way an operator's first setup would.
start() {
  dc create >/dev/null
  if [ "${1:-}" = seed ]; then
    docker run --rm --user 0 -v "${project}_state:/state" -v "$work/seed:/seed:ro" --entrypoint sh "$image" -c 'cp -a /seed/. /state/ && chown -R app:app /state'
  fi
  dc up -d >/dev/null
  wait_running
}
served_fingerprint() {
  echo | openssl s_client -connect localhost:20383 2>/dev/null | openssl x509 -noout -fingerprint -sha256 | sed 's/.*=//' | tr -d ':'
}

echo "== the image"
docker image inspect --format 'size: {{.Size}} bytes' "$image"
if docker run --rm --entrypoint sh "$image" -c 'command -v docker' >/dev/null; then fail "the image carries a docker binary"; fi
[ -z "$(docker run --rm --entrypoint find "$image" / -xdev -iname 'Avalonia*.dll')" ] || fail "the image carries an Avalonia assembly"
compose_config=$(dc config)
if grep -q 'docker.sock' <<< "$compose_config"; then fail "compose mounts the docker socket"; fi

echo "== controls: the checks above must be able to go red"
docker run -d --name "$project-control-root" --user 0 --entrypoint bash "$image" -c 'exec -a /app/Cockpit.Server sleep 60' >/dev/null
[ "$(server_uid "$project-control-root")" = 0 ] || fail "control: the server-uid check did not see a server that runs as root"
docker rm -f "$project-control-root" >/dev/null
printf 'FROM %s\nARG S\nENV LEAK=$S\n' "$image" | docker build -q --build-arg S="$unlock" -t "$project-control-leak" - >/dev/null
history_leaks "$project-control-leak" "$unlock" || fail "control: the history check missed a secret baked into a layer"
if history_leaks "$image" "$unlock"; then fail "the unlock secret is in an image layer"; fi
if history_leaks "$image" "$key"; then fail "the connect key is in an image layer"; fi

echo "== a fresh volume with both secrets"
start
first=$(fingerprint_of_log)
[ -n "$first" ] || fail "the log names no certificate fingerprint"
[ "$(served_fingerprint)" = "$first" ] || fail "the served certificate is not the one the log names"
status=$(curl -sk -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $key" https://localhost:20383/api/v1/whoami)
[ "$status" = 200 ] || fail "whoami with the connect key answered $status"
status=$(curl -sk -o /dev/null -w '%{http_code}' https://localhost:20383/api/v1/whoami)
[ "$status" != 200 ] || fail "whoami without a key answered 200"
log_has 'Worktree root set to /work/worktrees from COCKPIT_WORKTREE_ROOT.' || fail "the server did not take the worktree root from compose"
log_has 'Clone root set to /work/clones from COCKPIT_CLONE_ROOT.' || fail "the server did not take the clone root from compose"
container=$(dc ps -q cockpit)
uid=$(server_uid "$container")
[ -n "$uid" ] && [ "$uid" != 0 ] || fail "the server runs as uid '${uid:-none}'"
# The compose health check, run the way Docker runs it: as the container's user, which is now root (AC-1466).
health=$(docker inspect -f '{{index .Config.Healthcheck.Test 3}}' "$container")
dc exec -T cockpit node -e "$health" || fail "the compose health check fails"
collect_logs

echo "== agent sessions run as agent (AC-1464)"
# codex's npm entry is a node launcher, so the probe runs inside the process the wrapper started. claude is a native
# binary: it gets a plain start through its wrapper, which is the same file as codex's.
dc exec -T -u app cockpit sh -c 'cat > /work/smoke-probe.js' < deploy/smoke-probe.js
agent_uid=$(dc exec -T cockpit id -u agent)
tree=/work/worktrees/smoke
git_as() { echo "-c user.name=$1 -c user.email=$1@smoke"; }
dc exec -T -u app cockpit sh -c "umask 007 && git init -q $tree && git -C $tree $(git_as cockpit) commit -q --allow-empty -m init \
  && touch $tree/agent.txt && git -C $tree add agent.txt"
agent=$(probe codex -e SMOKE_WORKTREE=$tree)
echo "through the wrapper: $agent"
[ "$(field "$agent" uid)" = "$agent_uid" ] || fail "codex through the wrapper does not run as agent"
[ "$(field "$agent" pane)" = smoke-pane ] || fail "COCKPIT_PANE_ID did not reach the CLI through the wrapper"
[ "$(field "$agent" home)" = /home/agent ] || fail "the CLI's HOME is not the agent's"
for what in config certificate secret original environ; do
  [ "$(field "$agent" $what)" = EACCES ] || fail "an agent session can reach $what ($(field "$agent" $what))"
done
[ "$(field "$agent" worktree)" = committed ] || fail "an agent session cannot commit in a worktree under /work ($(field "$agent" worktree))"
dc exec -T -u app cockpit sh -c "umask 007 && echo cockpit >> $tree/agent.txt && git -C $tree $(git_as cockpit) commit -q -am cockpit && rm -rf $tree" \
  || fail "the cockpit cannot commit in or remove a worktree the agent wrote in"
dc exec -T -u app cockpit claude --version || fail "claude does not start through its wrapper"
# A sign-in writes its file as agent, owner-only; the server's login check only asks whether it exists (AC-1357).
dc exec -T -u agent cockpit sh -c 'umask 077 && : > /home/agent/.claude/.smoke-login'
dc exec -T -u app cockpit test -e /home/app/.claude/.smoke-login || fail "the server cannot see a file the agent's CLI wrote"
dc exec -T -u agent cockpit rm /home/agent/.claude/.smoke-login

echo "== controls: without the wrapper the probe reads it all, and a bare sudo loses the session env"
direct=$(probe /usr/local/bin/codex)
echo "without the wrapper: $direct"
for what in certificate secret environ; do
  [ "$(field "$direct" $what)" = read ] || fail "control: the server's own user cannot read $what ($(field "$direct" $what)), so the denial proves nothing"
done
bare=$(dc exec -T -u app -e COCKPIT_PANE_ID=smoke-pane cockpit \
  sudo -n -u agent NODE_OPTIONS=--require=/work/smoke-probe.js /usr/local/bin/codex --version | sed -n 's/^PROBE //p')
echo "a bare sudo: $bare"
[ "$(field "$bare" uid)" = "$agent_uid" ] || fail "control: the bare sudo did not run as agent"
[ "$(field "$bare" pane)" = null ] || fail "control: a bare sudo kept COCKPIT_PANE_ID, so the env check proves nothing"
dc exec -T -u app cockpit rm /work/smoke-probe.js

echo "== down and up on the same volume"
dc down >/dev/null
dc up -d >/dev/null
wait_running
[ "$(fingerprint_of_log)" = "$first" ] || fail "the fingerprint changed over a down and up with the same volume"
collect_logs

echo "== a new volume"
dc down -v >/dev/null
start
[ "$(fingerprint_of_log)" != "$first" ] || fail "control: a new volume kept the fingerprint"
collect_logs

echo "== a held node port stops the server"
dc down -v >/dev/null
docker run -d --name "$project-holder" --entrypoint node "$image" -e 'require("net").createServer().listen(20383); setInterval(() => {}, 1000)' >/dev/null
set +e
held=$(timeout 60 docker run --rm --network "container:$project-holder" -v "$COCKPIT_CONNECT_KEY_PATH:/run/secrets/key:ro" \
  -e COCKPIT_STATE_ROOT=/state -e COCKPIT_CONNECT_KEY_FILE=/run/secrets/key "$image" 2>&1)
code=$?
set -e
echo "$held" >> "$work/logs.txt"
docker rm -f "$project-holder" >/dev/null
[ "$code" != 0 ] && [ "$code" != 124 ] || fail "the server did not stop on a held node port (exit $code)"
if grep -qF 'Cockpit.Server running' <<< "$held"; then fail "the server reported running on a held node port"; fi
grep -qF 'the node door is not listening' <<< "$held" || fail "the held-port refusal names no reason"

echo "== encryption on: unlocks from the secret, refused without it"
dotnet run --project tests/Cockpit.ServerSeed --configuration Release -- "$work/seed" "$COCKPIT_UNLOCK_PASSWORD_PATH"
start seed
log_has 'Unlocked the credentials from the password file.' || { dc logs --no-color cockpit | tail -n 15; fail "the server did not unlock from the secret"; }
collect_logs
dc down >/dev/null
set +e
refused=$(dc run --rm --no-deps -e COCKPIT_UNLOCK_PASSWORD_FILE= cockpit 2>&1)
code=$?
set -e
echo "$refused" >> "$work/logs.txt"
[ "$code" != 0 ] || fail "the server started without the unlock secret"
grep -qF 'COCKPIT_UNLOCK_PASSWORD_FILE is not set.' <<< "$refused" || fail "the refusal names no reason"

echo "== no secret in a log"
if grep -qF -e "$unlock" -e "$key" "$work/logs.txt"; then fail "a secret is in the container log"; fi
echo "smoke passed"
