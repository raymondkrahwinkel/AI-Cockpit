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

# Self-contained so the target machine needs no .NET install. Not single-file (a .app is a directory anyway, so
# folding the payload into one file buys nothing here) and not trimmed — trimming is the one that actually breaks
# Avalonia, whose XAML loading is reflection-driven. Single-file does work, with
# IncludeNativeLibrariesForSelfExtract; the Windows build is exactly that.
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

# --deep, because a .NET publish is not the layout codesign expects. Everything in Contents/MacOS counts as code
# to it — the native .dylib files (which the publish ships without an executable bit, so signing "the
# executables" missed them) and the managed .dll assemblies alike, and one unsigned item among them is enough for
# --verify --strict to reject the whole bundle ("code object is not signed at all / In subcomponent: …"). Signing
# the pieces by hand means enumerating what counts as code, which is a thing Apple's tooling already knows.
#
# Apple discourages --deep for a *distributed* app: it signs nested code with the same options, which is wrong
# for a bundle carrying frameworks or helpers with their own entitlements. This one carries neither — it is a
# flat payload — and for the ad-hoc signature that makes the app testable it is exactly right. A Developer ID
# build that gets notarised should sign each Mach-O deliberately, and that is the day to revisit this.
codesign --force --deep --timestamp --options runtime --entitlements "$entitlements" --sign "$identity" "$app"
codesign --verify --deep --strict --verbose=2 "$app"

# The .dmg, which is how a macOS app is handed to a person: open it, drag the app onto the Applications shortcut
# next to it, done. A zipped bundle works too and is what automation wants, but it leaves the operator holding a
# folder they have to know where to put — the disk image says where by showing them.
echo "Building the disk image…"
staging="$(mktemp -d)/AI-Cockpit"
mkdir -p "$staging"
cp -R "$app" "$staging/"
ln -s /Applications "$staging/Applications"

dmg="$repo_root/artifacts/macos/AI-Cockpit-$version-arm64.dmg"
if [ "$arch" != "arm64" ]; then
    dmg="$repo_root/artifacts/macos/AI-Cockpit-$version-$arch.dmg"
fi

rm -f "$dmg"
# UDZO: compressed and read-only, which is what a download should be. -ov because a rebuild replaces the last one.
hdiutil create -volname "AI-Cockpit" -srcfolder "$staging" -ov -format UDZO "$dmg" >/dev/null

# The image itself is signed as well: an unsigned .dmg is one more thing for Gatekeeper to object to before the
# app inside it ever gets a chance to be judged on its own signature.
codesign --force --sign "$identity" "$dmg"

echo
echo "Done: $app"
echo "      $dmg"
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
