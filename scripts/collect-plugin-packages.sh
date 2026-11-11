#!/usr/bin/env bash
set -euo pipefail
count=0
failed=0
for candidate in prepared/prepared-plugin-*; do
  metadata="$candidate/publish.json"
  PLUGIN="$(jq -r '.plugin' "$metadata")"
  MANIFEST="$(jq -r '.manifest' "$metadata")"
  DIR="$(dirname "$MANIFEST")"
  VERSION="$(jq -r '.version' "$MANIFEST")"
  MIN_HOST="$(jq -r '.minHostVersion // ""' "$MANIFEST")"
  REL="$(jq -r '.rel' "$metadata")"
  SHA="$(jq -r '.sha' "$metadata")"
  PACKAGES="$(jq -r '.packages' "$metadata")"
  NOTES="$(jq -r '.notes' "$metadata")"
  SUBPROCESS="$(jq -r '.subprocess' "$metadata")"
  export PLUGIN MANIFEST DIR VERSION MIN_HOST REL SHA PACKAGES NOTES SUBPROCESS

  if jq -e --arg id "$PLUGIN" --arg v "$VERSION" \
    '.plugins[]? | select(.id==$id) | .versions[]? | select(.version==$v)' store/index.json >/dev/null; then
    echo "::notice::$PLUGIN $VERSION is already published; keeping its immutable packages."
    continue
  fi

  # An invalid candidate must not leave its index entry in the batch.
  cp store/index.json "$candidate/index.json"
  if STORE="$candidate" bash scripts/update-and-verify-plugin.sh; then
    cp "$candidate/index.json" store/index.json
    mkdir -p "store/$PLUGIN"
    cp "$candidate/$PLUGIN/"*.zip "store/$PLUGIN/"
    printf '%s %s\n' "$PLUGIN" "$VERSION" >> "$RUNNER_TEMP/published-plugins.txt"
    count=$((count + 1))
  else
    echo "::error::Rejecting $PLUGIN $VERSION; continuing with the other plugins."
    failed=$((failed + 1))
  fi
done
echo "count=$count" >> "$GITHUB_OUTPUT"
echo "failed=$failed" >> "$GITHUB_OUTPUT"
