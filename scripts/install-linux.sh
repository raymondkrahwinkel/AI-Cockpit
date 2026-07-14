#!/usr/bin/env bash
# Installs the cockpit into the Linux desktop for the current user: a launcher entry and an icon.
#
# Why this exists: `dotnet publish` leaves an executable in a directory, which a desktop environment knows
# nothing about — no entry in the application menu, no icon in the dock, and an Alt-Tab window that falls back
# to a generic placeholder. The desktop reads that identity from a .desktop file plus icons in the hicolor
# theme, so this puts them where the freedesktop spec says they live.
#
# Per-user (~/.local/share) rather than system-wide (/usr/share): it needs no root, and the cockpit is a
# single-operator tool.
#
# Usage:   scripts/install-linux.sh [path-to-executable]
# Default: publishes a self-contained build into artifacts/linux and points the launcher at that.
# Uninstall: scripts/install-linux.sh --uninstall

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/Cockpit.App/Cockpit.App.csproj"
desktop_source="$repo_root/packaging/linux/ai-cockpit.desktop"
icons_source="$repo_root/packaging/linux/icons"

apps_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
icons_dir="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"
desktop_target="$apps_dir/ai-cockpit.desktop"

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$desktop_target"
    find "$icons_dir" -name "ai-cockpit.png" -delete 2>/dev/null || true
    command -v update-desktop-database >/dev/null && update-desktop-database "$apps_dir" || true
    echo "Removed the launcher and its icons."
    exit 0
fi

executable="${1:-}"
if [ -z "$executable" ]; then
    output="$repo_root/artifacts/linux"
    echo "Publishing to $output…"
    # Self-contained so the machine needs no .NET install. Not single-file and not trimmed: both break
    # Avalonia's native libraries and reflection-driven XAML loading (same reasoning as package-macos.sh).
    dotnet publish "$project" \
        --configuration Release \
        --runtime linux-x64 \
        --self-contained true \
        -p:PublishSingleFile=false \
        -p:PublishTrimmed=false \
        --output "$output"
    executable="$output/Cockpit.App"
fi

if [ ! -x "$executable" ]; then
    echo "No executable at $executable — build first, or pass the path as an argument." >&2
    exit 1
fi
executable="$(cd "$(dirname "$executable")" && pwd)/$(basename "$executable")"

# The icons are checked in at each size the hicolor theme wants, so this install needs no image tooling on the
# machine. Regenerate them with scripts/generate-appicon.py when the icon changes.
for icon in "$icons_source"/*.png; do
    size="$(basename "$icon" .png)"
    target_dir="$icons_dir/${size}x${size}/apps"
    mkdir -p "$target_dir"
    cp "$icon" "$target_dir/ai-cockpit.png"
done

# Exec must be an absolute path: the desktop launches it with an unpredictable working directory, and a bare
# name only works for something already on PATH.
mkdir -p "$apps_dir"
sed "s|^Exec=.*|Exec=$executable|" "$desktop_source" > "$desktop_target"
chmod +x "$desktop_target"

# Both caches are advisory — the entry appears without them, but a stale cache can keep the old icon around.
command -v update-desktop-database >/dev/null && update-desktop-database "$apps_dir" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -f -t "$icons_dir" >/dev/null 2>&1 || true

echo "Installed:"
echo "  launcher  $desktop_target  ->  $executable"
echo "  icons     $icons_dir/<size>x<size>/apps/ai-cockpit.png"
