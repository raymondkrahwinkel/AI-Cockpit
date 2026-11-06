#!/usr/bin/env bash
# Builds the Browser plugin from a checkout, as the store would ship it for Linux, into <out-dir>: the managed plugin
# plus its self-contained linux-x64 Subprocess/ (CEF's child processes). It is not bundled in the app, so the smoke
# test installs it from here.
#
#   stage-browser.sh <repo-root> <out-dir>
set -euo pipefail
src="$(readlink -f "$1")/plugins-dev/Cockpit.Plugin.Browser"; out="$2"
rm -rf "$out"; mkdir -p "$out"; out="$(readlink -f "$out")"
dotnet build "$src/Cockpit.Plugin.Browser.csproj" --configuration Release -warnaserror -v q
dotnet publish "$src/Subprocess/Cockpit.Plugin.Browser.Subprocess.csproj" --configuration Release --runtime linux-x64 \
    --self-contained true -v q --output "$out/Subprocess"
for f in "$src"/bin/Release/net10.0/*; do
    [ "$(basename "$f")" = Subprocess ] || cp -r "$f" "$out/"
done
cp "$src/plugin.json" "$out/plugin.json"
