#!/usr/bin/env bash

# Classifies a diff into src/plugins_dev/other flags for ci.yml's job-level `if:` skips (see the `changes`
# job in ci.yml for why skipping happens that way and not via the `on:` trigger). `other` is the safe
# default: anything unrecognized forces every downstream job to run, same as a src/ change would.

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
