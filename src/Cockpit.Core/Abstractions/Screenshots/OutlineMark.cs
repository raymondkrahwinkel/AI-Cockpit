namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// A frame drawn around part of the picture (AC-359) — the first tool on the mark layer, and the way an operator
/// says look here rather than look at all of this.
/// </summary>
/// <param name="Area">The rectangle it frames, in the pixels of whichever image it is being spoken about in.</param>
/// <param name="Colour">Its colour as 0xAARRGGBB. Carried on the mark because the theme lives in the app and the burning-in happens where the imaging library does; passing the value keeps the one from having to know the other.</param>
/// <param name="Thickness">How thick the frame is, in the image's pixels.</param>
public sealed record OutlineMark(CaptureRect Area, uint Colour, int Thickness) : Mark
{
    /// <summary>
    /// Moved into the crop's space but deliberately not shrunk to it. Shrinking a frame draws a line along the
    /// crop edge that the operator never drew — the box would appear closed on a side where it actually ran off
    /// the picture. Translated whole instead, and the edges that fall outside are simply not painted.
    /// </summary>
    public override Mark? ClipTo(CaptureRect region) =>
        Area.Overlap(region) is null
            ? null
            : this with { Area = Area with { X = Area.X - region.X, Y = Area.Y - region.Y } };
}
