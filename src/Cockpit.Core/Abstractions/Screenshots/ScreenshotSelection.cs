namespace Cockpit.Core.Abstractions.Screenshots;

// What the operator settled on (AC-331, AC-359): the region they marked out, and everything they put on it.
// The two travel together because they are applied in order — crop first, marks second — so a mark is in the
// coordinates of the image that is actually sent rather than of the desktop it was drawn on.
public sealed record ScreenshotSelection
{
    // The region of the capture to take, in the capture's own pixels.
    public required CaptureRect Region { get; init; }

    // What was placed on it, in the cropped image's pixels, in the order it was placed. Empty when the operator
    // marked nothing. Order is kept because it is visible: a frame over a pixelated box does not look like a
    // pixelated box over a frame.
    public IReadOnlyList<Mark> Marks { get; init; } = [];
}
