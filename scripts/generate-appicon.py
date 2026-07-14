#!/usr/bin/env python3
"""Regenerates the app icon from the theme tokens.

The icon is the empty-state tile the operator already knows: a rounded square, panel-dark, with "AI" in the
accent. Identity beats contrast here — the letters are what carries at 32px, and they are accent-on-dark
either way. The tile's edge does lose itself against a dark taskbar, so the border is a step lighter than
the in-app hairline, which the app's own tile never needs (it sits on our panel, not on someone's desktop).

Colours and the corner radius come from Styles/Theme.axaml (CockpitAccentColor, CockpitPanelBgColor, and
the tile's 14/56 radius); keep them in step if the theme moves.

Usage: python3 scripts/generate-appicon.py     (needs Pillow and the Inter font)
Writes: src/Cockpit.App/Assets/AppIcon.png  (1024x1024 master — package-macos.sh builds the .icns from it)
        src/Cockpit.App/Assets/AppIcon.ico  (16-256px — the window icon and the Windows executable's icon)
        packaging/linux/icons/<size>.png    (hicolor sizes, so install-linux.sh only has to copy — a desktop
                                             install should not need Pillow or ImageMagick on the machine)

The macOS master is inset: Apple's icon grid puts a rounded-rect app at ~824px of a 1024px canvas and the
Dock sizes every icon by its canvas, so a full-bleed tile would stand a head taller than everything beside
it. Windows and the Linux hicolor theme want the opposite — they scale the whole canvas into a slot, and a
margin there just renders the icon small. Hence one shape, two framings.
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ACCENT = (217, 119, 87, 255)  # CockpitAccentColor   #D97757
PANEL_BG = (30, 33, 40, 255)  # CockpitPanelBgColor  #1E2128
BORDER = (58, 62, 71, 255)  # a step above CockpitHairlineColor (#2C2F37): a hairline is a rounding error
# at 32px, and the tile has no panel behind it to sit on out there
BORDER_WIDTH = 0.025  # of the tile, so it survives the downscale to a taskbar size

SIZE = 1024
RADIUS = int(SIZE * 14 / 56)  # the empty-state tile's CornerRadius, as a fraction of its size
GLYPH_HEIGHT = 0.44  # cap height as a fraction of the tile, eyeballed against the in-app tile
MACOS_TILE = 824  # Apple's icon grid: the rounded-rect footprint inside the 1024px canvas

FONT = "/usr/share/fonts/rsms-inter-fonts/InterDisplay-Bold.ttf"  # the app ships Avalonia.Fonts.Inter
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
HICOLOR_SIZES = [16, 32, 48, 64, 128, 256, 512]

repo = Path(__file__).resolve().parent.parent
assets = repo / "src" / "Cockpit.App" / "Assets"
hicolor = repo / "packaging" / "linux" / "icons"


def render() -> Image.Image:
    icon = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(icon)
    draw.rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], RADIUS,
                           fill=PANEL_BG, outline=BORDER, width=int(SIZE * BORDER_WIDTH))

    font = ImageFont.truetype(FONT, int(SIZE * GLYPH_HEIGHT))
    left, top, right, bottom = draw.textbbox((0, 0), "AI", font=font)
    draw.text(((SIZE - (right - left)) / 2 - left, (SIZE - (bottom - top)) / 2 - top),
              "AI", font=font, fill=ACCENT)
    return icon


def inset_for_macos(icon: Image.Image) -> Image.Image:
    canvas = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    tile = icon.resize((MACOS_TILE, MACOS_TILE), Image.LANCZOS)
    offset = (SIZE - MACOS_TILE) // 2
    canvas.paste(tile, (offset, offset), tile)
    return canvas


def main() -> None:
    icon = render()
    inset_for_macos(icon).save(assets / "AppIcon.png")  # the .icns master; macOS only

    # Pillow downsamples each entry from the master rather than the previous entry, so the small sizes stay
    # crisp instead of compounding resampling loss.
    icon.save(assets / "AppIcon.ico", sizes=[(s, s) for s in ICO_SIZES])

    hicolor.mkdir(parents=True, exist_ok=True)
    for size in HICOLOR_SIZES:
        icon.resize((size, size), Image.LANCZOS).save(hicolor / f"{size}.png")

    print(f"wrote {assets / 'AppIcon.png'} (macOS, inset), {assets / 'AppIcon.ico'} "
          f"and {len(HICOLOR_SIZES)} hicolor sizes in {hicolor}")


if __name__ == "__main__":
    main()
