namespace Cockpit.Core.Abstractions.Screenshots;

// A box the operator does not want leaving the machine (AC-331), pixelated into the image when the shot is
// confirmed. One mark type among the others since AC-359, on the same list and the same undo.
//
// `Area`: Where it sits, in the pixels of whichever image it is being spoken about in.
public sealed record RedactionMark(CaptureRect Area) : Mark
{
    public override Mark? ClipTo(CaptureRect region) =>
        Area.Overlap(region) is { } shared
            ? this with { Area = shared with { X = shared.X - region.X, Y = shared.Y - region.Y } }
            : null;
}
