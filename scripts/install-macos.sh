#!/usr/bin/env bash
# AC-663: installs a published build with curl, so the bundle never gets the quarantine flag Gatekeeper refuses
# an ad-hoc signature over. Do not swap this for `xattr -cr`: that wipes the extended attributes holding each
# managed .NET assembly's signature and leaves the app unsigned.
#
# Usage:   curl -fsSL https://raw.githubusercontent.com/raymondkrahwinkel/AI-Cockpit/main/scripts/install-macos.sh | bash
#          curl -fsSL …/install-macos.sh | bash -s -- nightly     # a release tag, instead of the latest stable

set -euo pipefail

repo="raymondkrahwinkel/AI-Cockpit"
tag="${1:-}"

if [ "$(uname -s)" != "Darwin" ]; then
    echo "This installs a macOS app and must run on macOS (found: $(uname -s))." >&2
    exit 1
fi

# Apple Silicon only, deliberately: the release ships one bundle per architecture and arm64 is the one it
# publishes. An x64 Mac cannot run it and Rosetta does not help, so say that rather than install something that
# fails to open.
if [ "$(uname -m)" != "arm64" ]; then
    echo "The published macOS build is Apple Silicon only (this machine is $(uname -m))." >&2
    echo "Build one for this architecture instead: scripts/package-macos.sh x64" >&2
    exit 1
fi

api="https://api.github.com/repos/$repo/releases/latest"
[ -n "$tag" ] && api="https://api.github.com/repos/$repo/releases/tags/$tag"

# Read the asset URL out of the release rather than composing a filename from a version: the name carries the
# version, and guessing it is how an installer breaks on the next release. grep and cut rather than jq, which a
# stock macOS does not have.
echo "Looking up the ${tag:-latest} release…"
# `|| true` on both: under `set -e` with pipefail a 404 from curl, or grep matching nothing, would abort the
# script with a bare exit code and skip the message below — which is the one that says what to do about it.
release="$(curl -fsSL "$api" || true)"
url="$(printf '%s' "$release" | grep -o '"browser_download_url"[^,]*macos-arm64\.app\.zip"' | cut -d'"' -f4 | head -1 || true)"

if [ -z "$url" ]; then
    echo "No macos-arm64 .app.zip in the ${tag:-latest} release of $repo." >&2
    echo "Releases: https://github.com/$repo/releases" >&2
    exit 1
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $(basename "$url")…"
curl -fL --progress-bar "$url" -o "$tmp/AI-Cockpit.app.zip"

# ditto, not unzip: it restores the bundle's symlinks and permission bits, and a .app that lost its executable
# bit is a .app that will not open. It also fails loudly on a truncated download, which is the only integrity
# check this needs.
ditto -x -k "$tmp/AI-Cockpit.app.zip" "$tmp/unpacked"

# /Applications is writable by an admin user, which the operator of a single-seat tool generally is. Falling
# back to ~/Applications rather than reaching for sudo: an installer that asks for a password to drop one app
# in a folder is asking for more than it needs.
target="/Applications"
[ -w "$target" ] || target="$HOME/Applications"
mkdir -p "$target"
app="$target/AI-Cockpit.app"

echo "Installing into ${target}…"
rm -rf "$app"
mv "$tmp/unpacked/AI-Cockpit.app" "$app"

# The whole point of the script, asserted rather than assumed. If a future macOS starts quarantining what curl
# fetched, this is where that shows up — as a failure here, not as "is damaged" three steps later.
if xattr -p com.apple.quarantine "$app" >/dev/null 2>&1; then
    echo >&2
    echo "WARNING: the bundle carries a quarantine flag anyway, so Gatekeeper will refuse it." >&2
    echo "Remove just that flag — not every attribute, which would strip the signature:" >&2
    echo "  xattr -dr com.apple.quarantine \"$app\"" >&2
    exit 1
fi

echo
echo "Done: $app"
echo "Open it from Finder or the Launchpad. To update, run this again."
