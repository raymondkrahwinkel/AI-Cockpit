namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// What happens to a capture between the operator marking it out and a session receiving it (AC-329): the crop,
/// then the marks placed on it (AC-331, AC-359). The order matters — the crop first, so a mark's coordinates
/// belong to the image that is actually sent. In Core because the caller is a view model and Core is what it may
/// reach; the implementation is Infrastructure's, where the imaging library already lives.
/// </summary>
public interface IScreenshotImageEditor
{
    /// <summary>
    /// The given region of the image, as PNG bytes. The region is in the image's own pixels and must lie inside
    /// it — a caller working from <see cref="ScreenCapture.Displays"/> already is.
    /// </summary>
    byte[] Crop(byte[] png, CaptureRect region);

    /// <summary>
    /// The image with every mark applied to the pixels themselves (AC-331, AC-359), in the order they were placed,
    /// so nothing that could travel separately from it survives. Destructive on purpose: an overlay would be a
    /// second thing that must travel with the image, and once the two can be separated — a preview showing one, an
    /// injection sending the other — the mark is gone unnoticed; for a redaction that's a leak, for a frame it's
    /// the model looking at the wrong thing. Redactions pixelate rather than blur (a weak gaussian is recoverable, coarse blocks aren't); a caller crops first and burns second.
    /// </summary>
    byte[] Burn(byte[] png, IReadOnlyList<Mark> marks);
}
