namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// What happens to a capture between the operator marking it out and a session receiving it (AC-329). Cropping
/// for now; redaction joins it (AC-331), and the order matters — the crop first, so a redaction's coordinates
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
    /// The image with every given rectangle pixelated beyond reading (AC-331), applied to the pixels themselves
    /// so the result carries no copy of what was there.
    /// </summary>
    /// <remarks>
    /// Destructive on purpose, and this is the whole point of the operation. An overlay drawn on top would be a
    /// second thing that has to travel with the image, and the moment the two can be separated — a preview that
    /// shows one and an injection that sends the other, a path that forgets to composite — the redaction is
    /// gone and nobody finds out. What goes to the model has to be the only version there is.
    /// <para>
    /// Pixelation rather than a blur: a weak gaussian is recoverable, and coarse blocks are not. The regions are
    /// in the image's own pixels, so a caller crops first and redacts second.
    /// </para>
    /// </remarks>
    byte[] Redact(byte[] png, IReadOnlyList<CaptureRect> regions);
}
