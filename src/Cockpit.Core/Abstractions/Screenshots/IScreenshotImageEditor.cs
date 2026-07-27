namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// What happens to a capture between the operator marking it out and a session receiving it (AC-329): the crop,
/// then the marks placed on it (AC-331, AC-359). The order matters — the crop first, so a mark's coordinates
/// belong to the image that is actually sent.
/// </summary>
/// <remarks>
/// In Core because the caller is a view model and Core is what it may reach; the implementation is
/// Infrastructure's, where the imaging library already lives.
/// </remarks>
public interface IScreenshotImageEditor
{
    /// <summary>
    /// The given region of the image, as PNG bytes. The region is in the image's own pixels and must lie inside
    /// it — a caller working from <see cref="ScreenCapture.Displays"/> already is.
    /// </summary>
    byte[] Crop(byte[] png, CaptureRect region);

    /// <summary>
    /// The image with every mark applied to the pixels themselves (AC-331, AC-359), in the order they were
    /// placed, so the result carries no copy of what was there and nothing that could travel separately from it.
    /// </summary>
    /// <remarks>
    /// Destructive on purpose, and this is the whole point of the operation. An overlay drawn on top would be a
    /// second thing that has to travel with the image, and the moment the two can be separated — a preview that
    /// shows one and an injection that sends the other, a path that forgets to composite — the mark is gone and
    /// nobody finds out. For a redaction that is a leak; for a frame it is the model looking at the wrong thing.
    /// What goes to the model has to be the only version there is.
    /// <para>
    /// Redactions pixelate rather than blur: a weak gaussian is recoverable, and coarse blocks are not. Marks are
    /// in the image's own pixels, so a caller crops first and burns second.
    /// </para>
    /// </remarks>
    byte[] Burn(byte[] png, IReadOnlyList<Mark> marks);
}
