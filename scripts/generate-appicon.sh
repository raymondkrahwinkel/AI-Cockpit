#!/usr/bin/env bash
#
# Regenerates the app icon (window icon, .ico, Linux hicolor set) from brand/ — the one place the
# Wispslate mark is composed and cleaned (scripts/generate-brand-icons.sh). This script no longer reads
# src/Cockpit.App/Assets/BrandMark.png or scales/crops it itself: brand/ is the only source, so there is
# one icon pipeline instead of two drifting apart (AC-446).
#
# The ladder splits at 24px: a taskbar-sized icon needs to sit on something against a dark taskbar, so
# 24px and up get the dark tile with the mark on it (AC-430). Below that the tile's own border and padding
# leave too little of the mark to read, so 16px uses the bare icon composition from brand/icons/blue/
# directly — already framed and cleaned there.
#
# Usage: scripts/generate-appicon.sh    (needs ImageMagick 7)
# Reads:  brand/wispslate-mark-blue.png, brand/icons/blue/16.png (scripts/generate-brand-icons.sh output)
# Writes: src/Cockpit.App/Assets/AppIcon.png  (1024x1024 macOS master, inset to Apple's icon grid)
#         src/Cockpit.App/Assets/AppIcon.ico  (16-256px — window icon and the Windows executable's icon)
#         packaging/linux/icons/<size>.png    (hicolor sizes, checked in so installs need no image tooling)
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
brand="$repo/brand"
assets="$repo/src/Cockpit.App/Assets"
hicolor="$repo/packaging/linux/icons"

COLOUR=blue # Cockpit; teal is Depot (brand/README.md)
MARK="$brand/wispslate-mark-$COLOUR.png"
BARE_16="$brand/icons/$COLOUR/16.png"

SIZE=1024
RADIUS=$((SIZE * 14 / 56)) # the empty-state tile's CornerRadius, as a fraction of its size
BORDER_WIDTH=$(awk -v s="$SIZE" 'BEGIN{printf "%d", s*0.025}') # survives the downscale to taskbar size
PANEL_BG="#1a1d24"  # CockpitPanelBgColor
BORDER="#3a3f49"    # a step above CockpitHairlineColor (#2a2f39) — a hairline is a rounding error at 32px
MARK_WIDTH_FRAC=0.82 # of the tile; the mark's own aspect decides its height, so it is never squared off
MACOS_TILE=824        # Apple's icon grid: the rounded-rect footprint inside the 1024px canvas

TILE_SPLIT=24 # >= this: dark tile with the mark on it. below: the bare mark from brand/icons/blue/
ICO_SIZES=(16 24 32 48 64 128 256)
HICOLOR_SIZES=(16 32 48 64 128 256 512)

QUANTISE=(-depth 8)
PNG_OPTS=(-quality 95 -define png:color-type=6 -define png:exclude-chunks=date,time)

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

for f in "$MARK" "$BARE_16"; do
  if [ ! -f "$f" ]; then
    echo "$f is missing — run scripts/generate-brand-icons.sh first." >&2
    exit 1
  fi
done

# Pack an .ico container the same way scripts/generate-brand-icons.sh does: ImageMagick's own .ico writer
# stores every entry as an uncompressed bitmap, which is several times the size of keeping them as PNGs.
_u8() { printf '%b' "\\$(printf '%03o' "$1")"; }
_u16() { _u8 $(($1 & 255)); _u8 $((($1 >> 8) & 255)); }
_u32() { _u16 $(($1 & 65535)); _u16 $((($1 >> 16) & 65535)); }

_pack_ico() {
  # $1 out.ico  $2.. png files, smallest first
  local out="$1" offset size width height png
  shift
  offset=$((6 + 16 * $#))
  {
    _u16 0
    _u16 1
    _u16 $#
    for png in "$@"; do
      width=$(magick identify -format %w "$png")
      height=$(magick identify -format %h "$png")
      size=$(wc -c <"$png")
      _u8 $((width % 256))
      _u8 $((height % 256))
      _u8 0
      _u8 0
      _u16 1
      _u16 32
      _u32 "$size"
      _u32 "$offset"
      offset=$((offset + size))
    done
    cat "$@"
  } >"$out"
}

# The tile master: dark rounded square, the cleaned mark (brand/'s own, artefact-free) centred on it,
# scaled by width so it is never stretched. Rendered once at 1024 and downsampled per size below, so the
# small entries stay crisp instead of compounding resampling loss.
magick "$MARK" -trim +repage "$work/mark.png"
magick -size "${SIZE}x${SIZE}" xc:none \
  -fill "$PANEL_BG" -stroke "$BORDER" -strokewidth "$BORDER_WIDTH" \
  -draw "roundrectangle 0,0,$((SIZE - 1)),$((SIZE - 1)),$RADIUS,$RADIUS" \
  \( "$work/mark.png" -resize "$(awk -v s="$SIZE" -v f="$MARK_WIDTH_FRAC" 'BEGIN{printf "%d", s*f}')x" \) \
  -gravity center -compose over -composite \
  "$work/tile.png"

# macOS insets the tile to Apple's 824-of-1024 grid; Windows and the Linux hicolor theme scale the whole
# canvas into a slot, so they get the full-bleed tile instead.
magick -size "${SIZE}x${SIZE}" xc:none \( "$work/tile.png" -resize "${MACOS_TILE}x${MACOS_TILE}" \) \
  -gravity center -compose over -composite \
  "${QUANTISE[@]}" -background none -alpha background "${PNG_OPTS[@]}" \
  "$assets/AppIcon.png"

_render_size() {
  # $1 size  $2 out.png
  local size="$1" out="$2"
  if [ "$size" -lt "$TILE_SPLIT" ]; then
    cp "$BARE_16" "$out"
  else
    magick "$work/tile.png" -filter Lanczos -resize "${size}x${size}" \
      "${QUANTISE[@]}" -background none -alpha background "${PNG_OPTS[@]}" "$out"
  fi
}

mkdir -p "$work/ico"
ico_entries=()
for size in "${ICO_SIZES[@]}"; do
  _render_size "$size" "$work/ico/$size.png"
  ico_entries+=("$work/ico/$size.png")
done
_pack_ico "$assets/AppIcon.ico" "${ico_entries[@]}"

mkdir -p "$hicolor"
for size in "${HICOLOR_SIZES[@]}"; do
  _render_size "$size" "$hicolor/$size.png"
done

echo "wrote $assets/AppIcon.png (macOS, inset), $assets/AppIcon.ico and ${#HICOLOR_SIZES[@]} hicolor sizes in $hicolor"
