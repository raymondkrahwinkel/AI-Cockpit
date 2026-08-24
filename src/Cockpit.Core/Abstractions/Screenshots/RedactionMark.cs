namespace Cockpit.Core.Abstractions.Screenshots;

// AC-331: box the operator does not want leaving the machine, pixelated into the image on confirm. One mark
// type among others since AC-359, same list and undo. `Area`: where it sits, in the image's pixels.
public sealed record RedactionMark(CaptureRect Area) : Mark
{
    public override Mark? ClipTo(CaptureRect region) =>
        Area.Overlap(region) is { } shared
            ? this with { Area = shared with { X = shared.X - region.X, Y = shared.Y - region.Y } }
            : null;
}
