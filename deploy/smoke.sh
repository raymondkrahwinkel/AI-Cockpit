#!/usr/bin/env bash
# AC-1356: the container smoke, run by CI before the push. Usage: deploy/smoke.sh <image:tag> (from the repo root).
# Seeds a state, then proves: a start with both secrets answers on the node door; a restart keeps the fingerprint and a
# fresh volume does not; no unlock secret means a refusal; the image is non-root with no docker and no Avalonia; no
# secret is in a layer or a log. Each check that could pass vacuously has a control that must come out red.
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
# Docker file secrets keep the host's mode, and the container's user is not the runner's.
chmod 644 "$COCKPIT_UNLOCK_PASSWORD_PATH" "$COCKPIT_CONNECT_KEY_PATH"
: > "$work/logs.txt"

dc() { docker compose -p "$project" -f deploy/compose.yaml "$@"; }
fail() { echo "::error::smoke: $*"; exit 1; }
cleanup() { dc down -v --remove-orphans >/dev/null 2>&1 || true; docker rmi -f "$project-control-root" "$project-control-leak" >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT

is_non_root() { [ "$(docker run --rm --entrypoint id "$1" -u)" != 0 ]; }
history_leaks() { docker history --no-trunc --format '{{.CreatedBy}}' "$1" | grep -qF "$2"; }
collect_logs() { dc logs --no-color cockpit >> "$work/logs.txt" 2>&1 || true; }
fingerprint_of_log() { dc logs --no-color cockpit | sed -n 's/.*presents certificate fingerprint \([0-9A-Fa-f]*\)\..*/\1/p' | tail -n 1; }
wait_running() { timeout 60 sh -c "docker compose -p $project -f deploy/compose.yaml logs -f --no-color cockpit | grep -m1 -q 'Cockpit.Server running'"; }
# A fresh volume is created by compose, then given the seeded state the way an operator's first setup does.
start_seeded() {
  dc create >/dev/null
  docker run --rm --user 0 -v "${project}_state:/state" -v "$work/seed:/seed:ro" --entrypoint sh "$image" -c 'cp -a /seed/. /state/ && chown -R app:app /state'
  dc up -d >/dev/null
  wait_running || { dc logs --no-color cockpit | sed -e "s/$unlock/<unlock>/g" -e "s/$key/<key>/g"; fail "the server did not report running within 60 s"; }
}
served_fingerprint() {
  echo | openssl s_client -connect localhost:20383 2>/dev/null | openssl x509 -noout -fingerprint -sha256 | sed 's/.*=//' | tr -d ':'
}

echo "== the image"
is_non_root "$image" || fail "the image runs as uid 0"
if docker run --rm --entrypoint sh "$image" -c 'command -v docker' >/dev/null; then fail "the image carries a docker binary"; fi
[ -z "$(docker run --rm --entrypoint find "$image" / -xdev -iname 'Avalonia*.dll')" ] || fail "the image carries an Avalonia assembly"
dc config | grep -q 'docker.sock' && fail "compose mounts the docker socket"

echo "== controls: the checks above must be able to go red"
printf 'FROM %s\nUSER root\n' "$image" | docker build -q -t "$project-control-root" - >/dev/null
is_non_root "$project-control-root" && fail "control: the non-root check passed an image with USER root"
printf 'FROM %s\nARG S\nENV LEAK=$S\n' "$image" | docker build -q --build-arg S="$unlock" -t "$project-control-leak" - >/dev/null
history_leaks "$project-control-leak" "$unlock" || fail "control: the history check missed a secret baked into a layer"
history_leaks "$image" "$unlock" && fail "the unlock secret is in an image layer"
history_leaks "$image" "$key" && fail "the connect key is in an image layer"

echo "== dotnet seed"
dotnet run --project tests/Cockpit.ServerSeed --configuration Release -- "$work/seed" "$COCKPIT_UNLOCK_PASSWORD_PATH"

echo "== start with both secrets"
start_seeded
first=$(fingerprint_of_log)
[ -n "$first" ] || fail "the log names no certificate fingerprint"
[ "$(served_fingerprint)" = "$first" ] || fail "the served certificate is not the one the log names"
status=$(curl -sk -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $key" https://localhost:20383/api/v1/whoami)
[ "$status" = 200 ] || fail "whoami with the connect key answered $status"
collect_logs

echo "== restart on the same volume"
dc down >/dev/null
dc up -d >/dev/null; wait_running || fail "no restart within 60 s"
[ "$(fingerprint_of_log)" = "$first" ] || fail "the fingerprint changed over a down and up with the same volume"
collect_logs

echo "== a fresh volume"
dc down -v >/dev/null
start_seeded
[ "$(fingerprint_of_log)" != "$first" ] || fail "control: a fresh volume kept the fingerprint"
collect_logs

echo "== no unlock secret: fail closed"
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
