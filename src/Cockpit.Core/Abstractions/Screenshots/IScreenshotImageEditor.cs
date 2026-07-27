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
}
