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

# createdump is what the runtime shells out to when writing a crash/heap dump; missing it does not fail the
# dump, it just produces an empty file dotnet-dump calls "Complete" (AC-989). This cp is not the file's known
# loss point (that turned out to be `vpk pack`'s own exclude list), but a guard here is one line and catches
# any future publish/copy regression too.
if [ ! -x "$appdir/usr/bin/createdump" ]; then
    echo "createdump is missing (or not executable) in $appdir/usr/bin — .NET crash dumps would silently fail (AC-989)." >&2
    exit 1
fi

# The icon, at every size the desktop asks for. Checked in (scripts/generate-appicon.sh writes them), so this
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
#
# It copies usr/ into a versioned cache directory and execs from there instead of from the mount (AC-1116): once
# the process has mapped libcoreclr.so from the cache, a squashfuse mount that later dies (observed for 3 of 6
# mounts on this machine — AC-1114) can no longer SIGBUS it. Only the version just written is kept — one AppImage
# release is 232 MB in the cache, and nothing here is asked to manage more than "current". If the copy or the exec
# from cache fails for any reason, it falls back to running straight from the mount, i.e. today's behaviour.
printf '%s' "$version" > "$appdir/VERSION"
cat > "$appdir/AppRun" <<'APPRUN'
#!/usr/bin/env bash
here="$(dirname "$(readlink -f "$0")")"

run_from_mount() {
    export PATH="$here/usr/bin:$PATH"
    exec "$here/usr/bin/Cockpit.App" "$@"
}

version="$(cat "$here/VERSION" 2>/dev/null || true)"
[ -n "$version" ] || run_from_mount "$@"

cache_root="${XDG_CACHE_HOME:-$HOME/.cache}/Cockpit"
cache_dir="$cache_root/$version"
app="$cache_dir/usr/bin/Cockpit.App"

if [ ! -x "$app" ]; then
    tmp_dir="$cache_root/.tmp.$$"
    # rm -rf "$cache_dir" first: a half-written cache_dir from an interrupted run would otherwise make mv -T
    # fail forever, and every later start would silently fall back to the mount without ever healing.
    if rm -rf "$tmp_dir" "$cache_dir" && mkdir -p "$tmp_dir" && cp -r "$here/usr" "$tmp_dir/usr" && mv -T "$tmp_dir" "$cache_dir"; then
        # Reached only after a successful mv, so cache_dir already exists here — the glob below always
        # matches at least one directory and never falls through to its own unexpanded literal.
        for old in "$cache_root"/*/; do
            old="${old%/}"
            [ "$old" = "$cache_dir" ] || rm -rf "$old"
        done
    else
        rm -rf "$tmp_dir"
    fi
fi

if [ -x "$app" ]; then
    export PATH="$cache_dir/usr/bin:$PATH"
    exec "$app" "$@"
fi

run_from_mount "$@"
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
