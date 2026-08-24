namespace Cockpit.Core.Abstractions.Screenshots;

// AC-1013 (AC-363): note typed onto the capture, the only mark carrying meaning rather than emphasis;
// `At`/`Text`/`Colour`/`Size`, placed by corner not baseline. Deleted: rationale for the backing plate (bare
// letters would turn to mud at label sizes, unlike a ringed stroke) and why an empty `Text` is refused.
public sealed record TextMark(CapturePoint At, string Text, uint Colour, int Size) : Mark
{
    // How much room is left around the letters inside the plate, in multiples of their height. Enough that the plate reads as a label rather than as a box someone forgot to size.
    private const double PaddingInSizes = 0.35;

    // The plate behind the letters — the opposite shade, so the letters have one background they can be relied on to contrast with whatever the capture is doing underneath.
    public uint Plate => ContrastingWith(Colour);

    // How far the letters sit in from the plate's edge, in the image's pixels.
    public double Padding => Size * PaddingInSizes;

    // AC-1013: left whole rather than trimmed (a trimmed label says something other than typed) and dropped
    // only when clearly outside the region, since plate width depends on a font not known here.
    public override Mark? ClipTo(CaptureRect region) =>
        At.X < region.Right && At.Y < region.Bottom
        && At.X + Size > region.X && At.Y + Size > region.Y
            ? this with { At = new CapturePoint(At.X - region.X, At.Y - region.Y) }
            : null;
}
