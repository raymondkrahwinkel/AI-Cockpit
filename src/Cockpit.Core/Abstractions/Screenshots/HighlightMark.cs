namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// Which way a wash moves the pixels under it (AC-361). A marker pen only knows one direction — it darkens paper —
/// and paper is the only thing it is ever used on. A screenshot is not paper twice over: half of one is a white
/// document and the other half a black terminal.
/// </summary>
public enum HighlightBlend
{
    /// <summary>
    /// Multiplied into what is there, the way ink over paper works. Light background: the page takes the colour
    /// and the dark text on it stays dark, because multiplying a near-black by anything leaves it near-black.
    /// </summary>
    Darken,

    /// <summary>
    /// Lifted out of what is there — the same arithmetic upside down, for a background that is already dark. The
    /// band becomes visible and the light text on it stays lighter still.
    /// </summary>
    Lighten,
}

/// <summary>
/// A wash of colour over part of the picture (AC-361) — emphasis without hiding, which is the whole of what makes
/// it a different tool from the box that obscures.
/// </summary>
/// <param name="Area">The band it covers, in the pixels of whichever image it is being spoken about in.</param>
/// <param name="Colour">What it is drawn in as 0xAARRGGBB, carried for the same reason the other marks carry it.</param>
/// <param name="Blend">Which way it moves what is under it, decided from what is under it when the wash is placed.</param>
/// <remarks>
/// The blend is on the mark rather than worked out where it is drawn, because it is drawn twice: once as a preview
/// on the surface and once into the delivered picture. Deciding it in each place means two decisions that can
/// disagree, and the one the operator checks would then not be the one they send.
/// <para>
/// Plain transparency was the obvious implementation and is the wrong one. Compositing a colour at even a third of
/// its strength drags black text and white page towards each other — over 20:1 of contrast falls to about 3:1, and
/// what the tool exists for is emphasis <em>without</em> costing legibility. Multiplying keeps the ratio nearly
/// intact, because it scales both ends rather than pulling them to a middle.
/// </para>
/// </remarks>
public sealed record HighlightMark(CaptureRect Area, uint Colour, HighlightBlend Blend) : Mark
{
    /// <summary>
    /// How far the colour is mixed towards the end it is blending against — white for a wash that darkens, black
    /// for one that lifts. A marker pen is pale for a reason: at full strength a saturated ink stops being a wash
    /// over the text and starts being a coat of paint on top of it.
    /// </summary>
    private const double Paleness = 0.62;

    /// <summary>
    /// The colour actually blended, once it has been made pale enough to read through. Worked out here so the two
    /// places that draw this wash cannot arrive at different shades of it.
    /// </summary>
    public uint Wash => Blend == HighlightBlend.Darken
        ? _MixedTowards(0xFF, Paleness)
        : _MixedTowards(0x00, Paleness);

    /// <summary>
    /// Shrunk to the crop, the way a redaction box is: a wash is an area rather than a shape, so the part of it
    /// that falls outside the picture is simply not part of the picture, and the rest is still exactly the band
    /// the operator drew over what remains.
    /// </summary>
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
