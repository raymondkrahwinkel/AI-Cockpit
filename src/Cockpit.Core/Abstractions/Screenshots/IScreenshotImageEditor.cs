namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// What happens to a capture between the operator marking it out and a session receiving it (AC-329): crop, then marks
/// (AC-331, AC-359) — crop first, so a mark's coordinates belong to the image actually sent. In Core because the caller
/// is a view model; the implementation is Infrastructure's, where the imaging library lives.
/// </summary>
public interface IScreenshotImageEditor
{
    /// <summary>
    /// The given region of the image, as PNG bytes. The region is in the image's own pixels and must lie inside
    /// it — a caller working from <see cref="ScreenCapture.Displays"/> already is.
    /// </summary>
    byte[] Crop(byte[] png, CaptureRect region);

    /// <summary>
    /// The image with every mark burned into the pixels (AC-331, AC-359), in placement order. Destructive on purpose: an
    /// overlay could separate from the image unnoticed — a leak for a redaction, wrong context for a frame. Redactions
    /// pixelate, not blur (gaussian is recoverable); a caller crops first and burns second.
    /// </summary>
    byte[] Burn(byte[] png, IReadOnlyList<Mark> marks);
}
