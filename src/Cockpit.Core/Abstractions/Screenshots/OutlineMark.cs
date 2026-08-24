namespace Cockpit.Core.Abstractions.Screenshots;

// AC-359: a frame drawn around part of the picture, the first mark-layer tool. `Area`/`Colour`/`Thickness`;
// colour is carried on the mark so the imaging library (where burning-in happens) needn't know the app's theme.
public sealed record OutlineMark(CaptureRect Area, uint Colour, int Thickness) : Mark
{
    // Moved into the crop's space but deliberately not shrunk to it. Shrinking a frame draws a line along the
    // crop edge that the operator never drew — the box would appear closed on a side where it actually ran off
    // the picture. Translated whole instead, and the edges that fall outside are simply not painted.
    public override Mark? ClipTo(CaptureRect region) =>
        Area.Overlap(region) is null
            ? null
            : this with { Area = Area with { X = Area.X - region.X, Y = Area.Y - region.Y } };
}
