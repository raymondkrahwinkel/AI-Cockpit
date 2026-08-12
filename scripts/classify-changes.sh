#!/usr/bin/env bash
# Categorizes a diff's files into three flags so ci.yml can skip jobs unrelated to what changed, via
# job-level `if:` -- never by filtering the workflow's own `on:` trigger, which would leave a required
# status check waiting on a run that never happens (see the `changes` job comment in ci.yml).
#
# - src=true         -- something under src/ changed (Core/Infrastructure/App/Plugins.Abstractions)
# - plugins_dev=true -- something under plugins-dev/ changed
# - other=true       -- anything NOT under src/, plugins-dev/, docs/, or a *.md/CHANGELOG.md file
#
# `other` is the safe-default flag: an unrecognized file (the workflow itself, a root script,
# Directory.Build.props, ...) forces every downstream job to run, exactly like a src/ change would.
#
#   scripts/classify-changes.sh <base-ref> [head-ref]
set -euo pipefail

base="${1:?usage: classify-changes.sh <base-ref> [head-ref]}"
head="${2:-HEAD}"

src=false; plugins_dev=false; other=false

while IFS= read -r f; do
  [ -n "$f" ] || continue
  case "$f" in
    src/*) src=true ;;
    plugins-dev/*) plugins_dev=true ;;
    docs/*|*.md|CHANGELOG.md) ;;
    *) other=true ;;
  esac
done <<<"$(git diff --name-only "$base" "$head")"

printf 'src=%s\n' "$src"
printf 'plugins_dev=%s\n' "$plugins_dev"
printf 'other=%s\n' "$other"
