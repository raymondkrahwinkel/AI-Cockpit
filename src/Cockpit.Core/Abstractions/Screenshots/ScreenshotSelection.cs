namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// What the operator settled on (AC-331): the region they marked out, and the parts of it they do not want
/// leaving the machine.
/// </summary>
/// <remarks>
/// The two travel together because they are applied in order — crop first, redact second — so the boxes are in
/// the coordinates of the image that is actually sent rather than of the desktop they were drawn on.
/// </remarks>
public sealed record ScreenshotSelection
{
    /// <summary>The region of the capture to take, in the capture's own pixels.</summary>
    public required CaptureRect Region { get; init; }

    /// <summary>Boxes to obscure, in the cropped image's pixels. Empty when nothing was hidden.</summary>
    public IReadOnlyList<CaptureRect> Redactions { get; init; } = [];
}
