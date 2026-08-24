namespace Cockpit.Core.Abstractions.Screenshots;

// Which way a wash moves the pixels under it (AC-361). A marker pen only knows one direction — it darkens paper —
// and paper is the only thing it is ever used on. A screenshot is not paper twice over: half of one is a white
// document and the other half a black terminal.
public enum HighlightBlend
{
    // Multiplied into what is there, the way ink over paper works. Light background: the page takes the colour
    // and the dark text on it stays dark, because multiplying a near-black by anything leaves it near-black.
    Darken,

    // Lifted out of what is there — the same arithmetic upside down, for a background that is already dark. The
    // band becomes visible and the light text on it stays lighter still.
    Lighten,
}

// AC-361: colour wash over part of the picture, `Area`/`Colour`/`Blend` (decided when placed, stored on the
// mark since it's drawn twice — preview and delivered picture — and must agree). Plain transparency was
// rejected: it drags black text and white page together (20:1 contrast to ~3:1); multiplying keeps ratio intact.
public sealed record HighlightMark(CaptureRect Area, uint Colour, HighlightBlend Blend) : Mark
{
    // How far the colour is mixed towards the end it is blending against — white for a wash that darkens, black
    // for one that lifts. A marker pen is pale for a reason: at full strength a saturated ink stops being a wash
    // over the text and starts being a coat of paint on top of it.
    private const double Paleness = 0.62;

    // The colour actually blended, once it has been made pale enough to read through. Worked out here so the two
    // places that draw this wash cannot arrive at different shades of it.
    public uint Wash => Blend == HighlightBlend.Darken
        ? _MixedTowards(0xFF, Paleness)
        : _MixedTowards(0x00, Paleness);

    // Shrunk to the crop, the way a redaction box is: a wash is an area rather than a shape, so the part of it
    // that falls outside the picture is simply not part of the picture, and the rest is still exactly the band
    // the operator drew over what remains.
    public override Mark? ClipTo(CaptureRect region) =>
        Area.Overlap(region) is { } shared
            ? this with { Area = shared with { X = shared.X - region.X, Y = shared.Y - region.Y } }
            : null;

    private uint _MixedTowards(byte end, double howFar)
    {
        var red = _Mix((byte)((Colour >> 16) & 0xFF), end, howFar);
        var green = _Mix((byte)((Colour >> 8) & 0xFF), end, howFar);
        var blue = _Mix((byte)(Colour & 0xFF), end, howFar);

        return 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;
    }

    private static byte _Mix(byte from, byte to, double howFar) => (byte)Math.Round(from + ((to - from) * howFar));
}
