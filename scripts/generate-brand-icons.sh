#!/usr/bin/env bash
#
# Regenerates the Wispslate icon set from the single render both product colours derive from.
#
# The two icons that came out of the logo sheet were separate renders: different canvases, different
# aspect ratios, and a W drawn at different proportions in each. Laid over one another they read as two
# pictures rather than one mark in two colours, and neither was square, so every square target either
# stretched the mark or shrank it inside letterbox bars. One render is therefore the master and the
# second colourway is derived from it, which is the only way "overlay them and only the colour differs"
# can hold. Blue is the master: the teal render is clipped along its top edge (solid pixels sit on row 1
# of its canvas) and is the smaller of the two, so it cannot carry the geometry.
#
# Two square compositions come out of that one render, in both colours:
#
#   wispslate-mark-<colour>.png   the whole mark — motion streaks, W, plate — inside the square
#   wispslate-icon-<colour>.png   the W standing on its plate, framed for use as an icon
#
# The mark is 1.66:1, so in a square frame it can only ever be a wide, thin band with empty space above
# and below it. That is fine when it is drawn large and it is what the brand bar shows; as an icon it
# wastes half the pixels it is given, and at 32px and below the band collapses into a smudge. The icon
# composition frames the same artwork closer, which draws the mark 1.3x taller in the same number of
# pixels (measured: it spans 0.695 of the icon's height against 0.531 of the mark's), and lets the
# streaks run off the left edge rather than shrinking to fit. Every size in icons/ and every entry in
# the .ico comes from the icon composition — one drawing per colour rather than a set that changes
# picture halfway up the ladder.
#
# Usage: scripts/generate-brand-icons.sh    (needs ImageMagick 7)
#
# Everything below brand/ is output.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
brand="$repo/brand"
# The app already carries this render: the title bar and the About dialog draw it, and
# scripts/generate-appicon.py builds the tile icon from it (AC-430). Reading it there rather than keeping
# a second copy under brand/ means the repository holds the mark once. The dependency points the wrong
# way round — a product asset ought to come out of brand/ rather than the other way about — but turning
# it round means regenerating what generate-appicon.py writes, which is that script's business.
src="$repo/src/Cockpit.App/Assets/BrandMark.png"

SRC_W=276
SRC_H=206

# Both compositions are expressed as a box on the source canvas plus the fraction of the square that box
# should span. Framing against a box rather than the canvas keeps the glow free to hang outside it
# without dragging the artwork off centre, and lets the two compositions share one placement routine.
#
# MARK_BOX is the mark without its glow: the bounding box of every pixel at 50% alpha or more, measured
# after the cleanup below. ICON_BOX is the W and the plate it stands on, read off the source against a
# pixel grid — the streaks to its left fall outside the box and run off the edge of the square.
MARK_BOX="7,25,244,147" # x,y,w,h on the source canvas
MARK_FILL=0.88
ICON_BOX="57,20,195,136"
ICON_FILL=0.92

# 1024 is what a macOS .icns is built from, and the size this set has to be able to supply if it is ever
# to feed one — today scripts/package-macos.sh reads src/Cockpit.App/Assets/AppIcon.png, not this file,
# and that one is inset to Apple's 824-of-1024 grid where this is full-bleed, so it is not a drop-in.
# The mark has no consumer above 512 and the render behind it is 244px wide, so a 1024 canvas there
# would be half a megabyte of interpolation for a size nothing asks for.
ICON_MASTER=1024
MARK_MASTER=512

# Anything smaller than this many pixels, and not joined to the mark, is taken for an artefact.
ARTEFACT_LIMIT=200

ICO_SIZES=(16 24 32 48 64 128 256)
PNG_SIZES=(16 24 32 48 64 128 256 512) # 1024 is the master itself, so the ladder stops below it

# Written before the alpha is cleared, and the order is the whole point. ImageMagick is built at Q16
# here, so a pixel can carry an alpha of 1/65535 — invisible, but not zero. Clearing first leaves those
# alone, and dropping to 8 bits afterwards rounds their alpha to 0 while keeping the colour, which is
# how a file ends up full of fully transparent pixels that are nonetheless bright blue. Quantise first
# and they are genuinely zero by the time the clear runs. (Measured: reversing these two lines puts
# stray colour in 2-24% of the pixels of every file in this folder.)
QUANTISE=(-depth 8)

# 8 bits per channel because 16 is four times the bytes for precision no icon target reads. Colour type
# 6 is forced because ImageMagick otherwise stores the smallest entries as palette PNGs — lossless at
# those sizes, but every .ico directory entry declares 32 bits per pixel, and that declaration should be
# true of the payload behind it. The icon this sits beside is written the same way.
#
# The date and tIME chunks are excluded so a rerun that changes nothing writes the same bytes: these
# files are committed, and an icon set that turns up in every diff teaches everyone to ignore its diffs.
PNG_OPTS=(-quality 95 -define png:color-type=6 -define png:exclude-chunks=date,time)

# The teal colourway, transcribed from the original teal render's own palette: its dominant colours
# ordered by luminance, widened in the midtones because the master's lower strokes are violet (a mid
# luminance) where the teal render's are bright cyan. A luminance-to-colour map keeps every highlight
# and shadow of the master and replaces only the chroma, which is exactly the difference the pair is
# allowed to have. Each entry is width:from:to and the widths are the shape of the ramp.
TEAL_RAMP=(
  "28:#01203f:#01203f"
  "34:#01203f:#024D73"
  "38:#024D73:#0692AD"
  "50:#0692AD:#12BCCE"
  "56:#12BCCE:#37D1DE"
  "50:#37D1DE:#B6FCFD"
)

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# ImageMagick writes every .ico entry as an uncompressed bitmap: measured on this ladder, a 270kB entry
# for the 256px size inside a 373kB file, against 131kB for the whole thing with the PNGs kept as PNGs.
# Windows has read PNG-compressed entries since Vista and the icon this sits beside is built that way
# too, so the container is assembled here instead: a header, one 16-byte directory entry per size, then
# the PNG files themselves. A width or height byte of 0 means 256 — the field is a byte.
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
      size=$(wc -c <"$png") # not stat: BSD stat spells this -f%z, and macOS builds the .icns
      _u8 $((width % 256))
      _u8 $((height % 256))
      _u8 0 # no colour palette
      _u8 0 # reserved
      _u16 1  # colour planes
      _u16 32 # bits per pixel
      _u32 "$size"
      _u32 "$offset"
      offset=$((offset + size))
    done
    cat "$@"
  } >"$out"
}

_check_source() {
  # Every box below was measured on this one file. Against a different render they would still be valid
  # numbers and would frame the wrong thing, quietly — so refuse rather than crop something arbitrary.
  local actual
  actual=$(magick identify -format "%wx%h" "$src")
  if [ "$actual" != "${SRC_W}x${SRC_H}" ]; then
    echo "$src is ${actual}, expected ${SRC_W}x${SRC_H}." >&2
    echo "MARK_BOX and ICON_BOX were measured on that canvas; re-measure them before rerunning." >&2
    exit 1
  fi
}

_clean() {
  # Two artefacts sit in the render as islands of their own: a thin vertical line above the W and a
  # stray blob off the right edge, both survivors of the sheet it was cut from. They are the only things
  # in the image not connected to the mark, so dropping every component under 200px removes them and, on
  # this render, cannot touch the artwork — the mark is one component of 26137 pixels and the largest
  # thing dropped is 30. That is a property of this file rather than of the threshold, so what goes is
  # printed: a run that starts eating the artwork says so instead of quietly returning a smaller mark.
  # The mask is grown and softened first so the faint halo around what is kept survives the cut.
  local dropped
  dropped=$(magick "$src" -alpha extract -threshold 8% \
    -define connected-components:verbose=true \
    -define connected-components:area-threshold=1 \
    -connected-components 8 null: 2>/dev/null |
    awk -v limit="$ARTEFACT_LIMIT" \
      '$5 == "srgb(255,255,255)" && $4 < limit { print "  " $2, $3, $4 }')
  if [ -z "$dropped" ]; then
    # This render has artefacts; an empty list means the report stopped reading the component output
    # rather than that the image got cleaner, and the report is the only view of what the cut removes.
    echo "warning: nothing matched the artefact filter — check the connected-components output" >&2
  else
    echo "dropped as artefacts (bounding-box centroid area):"
    echo "$dropped"
  fi

  magick "$src" -alpha extract -threshold 8% \
    -define connected-components:area-threshold="$ARTEFACT_LIMIT" \
    -define connected-components:mean-color=true \
    -connected-components 8 -morphology Dilate Disk:4 -blur 0x1.5 "$work/keep.png"

  # -alpha background clears the colour channels of fully transparent pixels. The render leaves noise
  # there, invisible while the alpha is respected and visible the moment something flattens the image.
  magick "$src" \( +clone -alpha extract "$work/keep.png" -compose multiply -composite \) \
    -compose CopyOpacity -composite -background none -alpha background "$work/blue.png"
}

_recolour_teal() {
  local args=() width from to
  for stop in "${TEAL_RAMP[@]}"; do
    IFS=: read -r width from to <<<"$stop"
    args+=(\( -size "${width}x1" gradient:"$from-$to" \))
  done
  magick "${args[@]}" +append -resize 256x1! "$work/teal-clut.png"

  # Sigmoidal contrast before the map: the master's midtones are darker than the teal render's, and
  # lifting them here rather than in the ramp keeps the ramp readable as the palette it was taken from.
  magick "$work/blue.png" -alpha extract "$work/alpha.png"
  magick "$work/blue.png" -alpha off -colorspace Gray -sigmoidal-contrast 3x45% \
    "$work/teal-clut.png" -clut -colorspace sRGB \
    "$work/alpha.png" -alpha off -compose CopyOpacity -composite \
    -background none -alpha background "$work/teal.png"
}

_square() {
  # $1 in.png  $2 box(x,y,w,h)  $3 fill  $4 canvas  $5 out.png
  local in="$1" fill="$3" t="$4" out="$5" bx by bw bh f w h dx dy
  IFS=, read -r bx by bw bh <<<"$2"
  f=$(awk -v t="$t" -v fl="$fill" -v bw="$bw" 'BEGIN{printf "%.6f", t*fl/bw}')
  w=$(awk -v f="$f" -v s="$SRC_W" 'BEGIN{printf "%d", s*f+0.5}')
  h=$(awk -v f="$f" -v s="$SRC_H" 'BEGIN{printf "%d", s*f+0.5}')
  dx=$(awk -v f="$f" -v t="$t" -v bx="$bx" -v bw="$bw" 'BEGIN{printf "%d", t/2-(bx+bw/2)*f+0.5}')
  dy=$(awk -v f="$f" -v t="$t" -v by="$by" -v bh="$bh" 'BEGIN{printf "%d", t/2-(by+bh/2)*f+0.5}')
  # The master is drawn several times the size of the render it comes from, so the upscale gets a light
  # unsharp pass; without it the edges of the strokes go to mush at 512 and above. The downscales in
  # _ladder get none: at 24px and below it rings, and the ringing is the only thing you can see.
  magick -size "${t}x${t}" xc:none \
    \( "$in" -filter Lanczos -resize "${w}x${h}!" -unsharp 0x0.75+0.6+0.01 \) \
    -geometry "+${dx}+${dy}" -compose over -composite \
    "${QUANTISE[@]}" -background none -alpha background "${PNG_OPTS[@]}" "$out"
}

_ladder() {
  # $1 colour
  local colour="$1" dir="$brand/icons/$1" master="$brand/wispslate-icon-$1.png" entries=()
  mkdir -p "$dir"
  for size in "${PNG_SIZES[@]}"; do
    # Every size is resampled from the master rather than from the size above it, so the small entries
    # do not compound resampling loss.
    magick "$master" -filter Lanczos -resize "${size}x${size}" \
      "${QUANTISE[@]}" -background none -alpha background "${PNG_OPTS[@]}" "$dir/$size.png"
  done
  for size in "${ICO_SIZES[@]}"; do entries+=("$dir/$size.png"); done
  _pack_ico "$brand/icons/wispslate-$colour.ico" "${entries[@]}"
}

_check_source
_clean
_recolour_teal

for colour in blue teal; do
  _square "$work/$colour.png" "$MARK_BOX" "$MARK_FILL" "$MARK_MASTER" "$brand/wispslate-mark-$colour.png"
  _square "$work/$colour.png" "$ICON_BOX" "$ICON_FILL" "$ICON_MASTER" "$brand/wispslate-icon-$colour.png"
  _ladder "$colour"
done

echo "wrote 4 masters in $brand and ${#PNG_SIZES[@]} sizes plus an .ico per colour in $brand/icons"

# BrandIconAssetTests pins this hash, so a changed render fails a test rather than leaving brand/ quietly
# a generation behind. Printing it here is what makes that test's instruction ("replace the constant with
# the hash the script reports") something the reader can actually follow.
if command -v sha256sum >/dev/null 2>&1; then
  hash=$(sha256sum "$src" | cut -d' ' -f1)
else
  hash=$(shasum -a 256 "$src" | cut -d' ' -f1) # macOS ships shasum, not sha256sum
fi
echo "built from $src"
echo "  sha256 $hash"
