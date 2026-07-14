#!/usr/bin/env bash
# Packages the cockpit as a Linux AppImage: one file, no install, no dependencies.
#
# Why this exists: a tar.gz of a self-contained publish already runs anywhere, but it is a directory somebody has
# to keep somewhere, and it appears in no application menu. An AppImage is the same bytes with a desktop identity
# attached — double-click it and it runs, with its name and its icon, on any distribution.
#
# Usage:   scripts/package-appimage.sh [publish-dir] [version]
# Example: scripts/package-appimage.sh publish/linux-x64 0.3.0
# Default: publishes linux-x64 itself into artifacts/appimage/publish.
# Output:  artifacts/appimage/AI-Cockpit-<version>-x86_64.AppImage
#
# appimagetool is fetched if it is not on PATH. It is run with --appimage-extract-and-run because a CI runner (and
# plenty of desktops) have no FUSE, and without that flag the tool fails on the mount rather than on the build.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/Cockpit.App/Cockpit.App.csproj"
output="$repo_root/artifacts/appimage"
appdir="$output/AI-Cockpit.AppDir"

publish_dir="${1:-}"
version="${2:-$(dotnet msbuild "$project" -getProperty:VersionPrefix | tr -d '[:space:]')}"

if [ -z "$publish_dir" ]; then
    publish_dir="$output/publish"
    echo "Publishing linux-x64 (version $version)…"
    # Self-contained, not single-file and not trimmed: both break Avalonia's native libraries and its
    # reflection-driven XAML loading (the same reasoning as package-macos.sh).
    dotnet publish "$project" \
        --configuration Release \
        --runtime linux-x64 \
        --self-contained true \
        -p:PublishSingleFile=false \
        -p:PublishTrimmed=false \
        -p:Version="$version" \
        --output "$publish_dir"
fi

if [ ! -x "$publish_dir/Cockpit.App" ]; then
    echo "No published cockpit at $publish_dir/Cockpit.App." >&2
    exit 1
fi

echo "Building the AppDir…"
rm -rf "$appdir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications"

cp -r "$publish_dir/." "$appdir/usr/bin/"
chmod +x "$appdir/usr/bin/Cockpit.App"

# The icon, at every size the desktop asks for. Checked in (scripts/generate-appicon.py writes them), so this
# needs no image tooling on the machine that builds the AppImage.
for icon in "$repo_root"/packaging/linux/icons/*.png; do
    size="$(basename "$icon" .png)"
    target="$appdir/usr/share/icons/hicolor/${size}x${size}/apps"
    mkdir -p "$target"
    cp "$icon" "$target/ai-cockpit.png"
done

# AppImage looks for the icon and the .desktop file in the AppDir root as well, by convention.
cp "$repo_root/packaging/linux/icons/256.png" "$appdir/ai-cockpit.png"

# Exec is the plain name: the AppRun below puts usr/bin on PATH, and an absolute path baked in here would point at
# wherever this was built.
sed 's|^Exec=.*|Exec=ai-cockpit|' "$repo_root/packaging/linux/ai-cockpit.desktop" \
    > "$appdir/usr/share/applications/ai-cockpit.desktop"
cp "$appdir/usr/share/applications/ai-cockpit.desktop" "$appdir/ai-cockpit.desktop"

# AppRun is what runs when the AppImage is double-clicked. It has to resolve its own location: the mount point is
# different on every launch, and a hardcoded path would be a path that does not exist.
cat > "$appdir/AppRun" <<'APPRUN'
#!/usr/bin/env bash
here="$(dirname "$(readlink -f "$0")")"
export PATH="$here/usr/bin:$PATH"
exec "$here/usr/bin/Cockpit.App" "$@"
APPRUN
chmod +x "$appdir/AppRun"

tool="$(command -v appimagetool || true)"
if [ -z "$tool" ]; then
    tool="$output/appimagetool"
    if [ ! -x "$tool" ]; then
        echo "Fetching appimagetool…"
        curl -fsSL -o "$tool" \
            "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
        chmod +x "$tool"
    fi
fi

echo "Building the AppImage…"
target="$output/AI-Cockpit-$version-x86_64.AppImage"

# ARCH is not inferred from the AppDir's contents, and appimagetool refuses to guess.
ARCH=x86_64 "$tool" --appimage-extract-and-run "$appdir" "$target"

echo
echo "AppImage: $target"
echo "Run it with: chmod +x '$target' && '$target'"
