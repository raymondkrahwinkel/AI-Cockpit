namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// A note typed onto the capture (AC-363) — the only mark that carries meaning rather than emphasis. Every other
/// one says <em>look here</em>; this one says what to think when you do.
/// </summary>
/// <param name="At">The top-left corner of its backing plate, in the pixels of whichever image it is being spoken about in.</param>
/// <param name="Text">What it says. Never empty — a label with nothing on it is an invisible mark, and the surface refuses to place one.</param>
/// <param name="Colour">The colour of the letters as 0xAARRGGBB, carried for the same reason the other marks carry it.</param>
/// <param name="Size">How tall the letters are, in the image's pixels.</param>
/// <remarks>
/// Placed by its corner rather than by its baseline. A baseline is where a typographer puts text and a corner is
/// where an operator points: they click a spot and expect the note to start there, not to hang above it.
/// <para>
/// The plate is part of the mark rather than a second one. Bare letters over a screenshot are the same problem the
/// arrow has, one step worse — a stroke can be ringed, but ringing every glyph turns them to mud at the sizes a
/// label is read at. A plate behind them gives one known background, and then the letters only have to contrast
/// with that.
/// </para>
/// </remarks>
public sealed record TextMark(CapturePoint At, string Text, uint Colour, int Size) : Mark
{
    /// <summary>How much room is left around the letters inside the plate, in multiples of their height. Enough that the plate reads as a label rather than as a box someone forgot to size.</summary>
    private const double PaddingInSizes = 0.35;

    /// <summary>The plate behind the letters — the opposite shade, so the letters have one background they can be relied on to contrast with whatever the capture is doing underneath.</summary>
    public uint Plate => ContrastingWith(Colour);

    /// <summary>How far the letters sit in from the plate's edge, in the image's pixels.</summary>
    public double Padding => Size * PaddingInSizes;

    /// <summary>
    /// Moved into the crop's space and left whole, like the arrow and the stroke. A label trimmed at the edge is a
    /// label that says something other than what was typed, which is worse than one that runs off the picture.
    /// </summary>
    /// <remarks>
    /// Dropped only when its corner is well outside the region. How wide the plate is depends on the font that
    /// draws it, which is not known here — so what is asked is whether the label starts anywhere near the picture,
    /// and a label that starts inside it and runs off the right-hand edge keeps the part that fits.
    /// </remarks>
    public override Mark? ClipTo(CaptureRect region) =>
        At.X < region.Right && At.Y < region.Bottom
        && At.X + Size > region.X && At.Y + Size > region.Y
            ? this with { At = new CapturePoint(At.X - region.X, At.Y - region.Y) }
            : null;
}
