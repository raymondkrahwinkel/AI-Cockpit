#!/usr/bin/env bash
# Packages the cockpit as a macOS .app bundle.
#
# Why this exists: `dotnet publish` produces a bare executable, and macOS reads an app's identity from the
# bundle's Info.plist — without one the menu bar shows the Avalonia template's name ("Avalonia Application")
# instead of ours, there is no Dock icon, and the microphone permission prompt has nothing to say. A bundle is
# a directory with a fixed layout, so this builds it rather than taking on a packaging dependency.
#
# Usage:   scripts/package-macos.sh [arm64|x64] [version]
# Example: scripts/package-macos.sh arm64 1.2.0
# Output:  artifacts/macos/AI-Cockpit.app
#
# Run it on macOS: the publish targets a macOS runtime and the icon/signing steps use macOS-only tools.
# One bundle per architecture is deliberate. A universal binary means lipo-ing every native dylib in the
# publish output, not just the apphost — the half-done version of that ships a bundle that crashes on one of
# the two architectures.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/Cockpit.App/Cockpit.App.csproj"
plist_source="$repo_root/src/Cockpit.App/Info.plist"
entitlements="$repo_root/src/Cockpit.App/Cockpit.entitlements"
icon_master="$repo_root/src/Cockpit.App/Assets/AppIcon.png"

arch="${1:-$( [ "$(uname -m)" = "arm64" ] && echo arm64 || echo x64 )}"
version="${2:-1.0.0}"
runtime="osx-$arch"

app="$repo_root/artifacts/macos/AI-Cockpit.app"
contents="$app/Contents"

if [ "$(uname -s)" != "Darwin" ]; then
    echo "This script builds a macOS bundle and must run on macOS (found: $(uname -s))." >&2
    exit 1
fi

echo "Publishing $runtime (version $version)…"
rm -rf "$app"
mkdir -p "$contents/MacOS" "$contents/Resources"

# Self-contained so the target machine needs no .NET install. Not single-file and not trimmed: both break
# Avalonia's native libraries and reflection-driven XAML loading.
dotnet publish "$project" \
    --configuration Release \
    --runtime "$runtime" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    --output "$contents/MacOS"

cp "$plist_source" "$contents/Info.plist"
chmod +x "$contents/MacOS/Cockpit.App"

# Stamp the version the bundle actually is, rather than shipping whatever number the source plist happened to
# carry.
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version" "$contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $version" "$contents/Info.plist"

# Icon. macOS wants an .icns built from an iconset of exactly-named PNGs; the master must be 1024x1024, since
# icon_512x512@2x is that size. Without a master we drop CFBundleIconFile rather than leave the bundle pointing
# at a file that is not there — and we say so, instead of quietly shipping the generic icon.
if [ -f "$icon_master" ]; then
    echo "Building the icon…"
    iconset="$(mktemp -d)/AI-Cockpit.iconset"
    mkdir -p "$iconset"
    for size in 16 32 128 256 512; do
        sips -z "$size" "$size" "$icon_master" --out "$iconset/icon_${size}x${size}.png" >/dev/null
        sips -z "$((size * 2))" "$((size * 2))" "$icon_master" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
    done
    iconutil --convert icns "$iconset" --output "$contents/Resources/AI-Cockpit.icns"
else
    echo "WARNING: no icon master at $icon_master (needs a 1024x1024 PNG) — the bundle will use the generic icon." >&2
    /usr/libexec/PlistBuddy -c "Delete :CFBundleIconFile" "$contents/Info.plist"
fi

# Sign, inside-out: every Mach-O in the payload first, the bundle itself last. An unsigned bundle cannot be
# granted microphone access at all, so an ad-hoc signature is the minimum that makes the app testable — it is
# valid on this machine only. Pass a Developer ID identity as CODESIGN_IDENTITY to produce something
# distributable (then notarise, see below).
identity="${CODESIGN_IDENTITY:--}"
echo "Signing with identity: $identity"
find "$contents/MacOS" -type f -perm +111 -print0 |
    while IFS= read -r -d '' binary; do
        codesign --force --timestamp --options runtime --entitlements "$entitlements" --sign "$identity" "$binary"
    done
codesign --force --timestamp --options runtime --entitlements "$entitlements" --sign "$identity" "$app"
codesign --verify --strict --verbose=2 "$app"

echo
echo "Done: $app"
echo
if [ "$identity" = "-" ]; then
    cat <<'EOF'
This is an ad-hoc signature: it works on the machine that built it, but Gatekeeper will quarantine it anywhere
else. To distribute, sign with a Developer ID and notarise:

  CODESIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" scripts/package-macos.sh arm64 1.0.0
  ditto -c -k --sequesterRsrc --keepParent artifacts/macos/AI-Cockpit.app AI-Cockpit.zip
  xcrun notarytool submit AI-Cockpit.zip --keychain-profile "AC_PASSWORD" --wait
  xcrun stapler staple artifacts/macos/AI-Cockpit.app
EOF
fi
