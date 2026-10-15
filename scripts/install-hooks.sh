#!/usr/bin/env sh
# Wire the repo's tracked git hooks (hooks/) so they run on every commit.
#
# Uses core.hooksPath to point git at the tracked hooks/ directory directly — no copying,
# no symlinks. Idempotent: safe to run again at any time (e.g. after a fresh clone).
set -e

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

git config core.hooksPath hooks
chmod +x hooks/* 2>/dev/null || true

echo "git hooks wired: core.hooksPath=hooks"
