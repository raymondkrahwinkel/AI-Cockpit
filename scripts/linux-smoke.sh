#!/usr/bin/env bash
# Local Linux smoke test of the packaged AppImage (AC-2065). Not part of any GitHub workflow: run it by hand (Docker or a VM)
# before merging anything that touches a surface.
#
#   scripts/linux-smoke.sh <AppImage> [out-dir]            stage the Browser plugin, run in a container
#   scripts/linux-smoke.sh --no-docker <AppImage> [out-dir]  run on this machine (a VM; needs Xvfb, xdotool, ImageMagick)
#
# BROWSER_PLUGIN_DIR=<dir> uses an already staged Browser plugin (scripts/linux-smoke/stage-browser.sh) instead of
# building one from this checkout. Screenshots, the app log and summary.txt end up in <out-dir>; the exit code is 1
# when any step failed.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
use_docker=1
if [ "${1:-}" = "--no-docker" ]; then use_docker=0; shift; fi
appimage="$(readlink -f "${1:?usage: linux-smoke.sh [--no-docker] <AppImage> [out-dir]}")"
out="$(mkdir -p "${2:-$repo_root/artifacts/linux-smoke}" && readlink -f "${2:-$repo_root/artifacts/linux-smoke}")"
chmod +x "$appimage"

plugin="${BROWSER_PLUGIN_DIR:-}"
if [ -z "$plugin" ]; then
    plugin="$out/browser-plugin"
    "$repo_root/scripts/linux-smoke/stage-browser.sh" "$repo_root" "$plugin"
fi
plugin="$(readlink -f "$plugin")"

if [ $use_docker = 0 ]; then
    exec "$repo_root/scripts/linux-smoke/run.sh" "$appimage" "$plugin" "$out/run"
fi

# Git Bash on Windows hands docker POSIX-looking paths it cannot mount; elsewhere the paths are used as they are.
export MSYS_NO_PATHCONV=1
host_path() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else echo "$1"; fi; }

docker build --quiet --tag cockpit-linux-smoke "$(host_path "$repo_root/scripts/linux-smoke")"
# No /dev/dri, so there is no GPU. The AppImage and plugin are mounted read-only; shm is generous because Chromium
# needs it.
docker run --rm --shm-size=1g --env SMOKE_ENGINE_WAIT \
    --volume "$(host_path "$repo_root/scripts/linux-smoke"):/smoke:ro" \
    --volume "$(host_path "$appimage"):/in/Cockpit.AppImage:ro" \
    --volume "$(host_path "$plugin"):/in/browser-plugin:ro" \
    --volume "$(host_path "$out"):/out" \
    cockpit-linux-smoke /smoke/run.sh /in/Cockpit.AppImage /in/browser-plugin /out/run
