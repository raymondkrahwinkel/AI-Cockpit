using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// Records what was asked of the editor and hands the bytes straight back (AC-329). The crop itself is Skia's
/// and is tested where it lives; what these tests care about is whether a coordinator asked for one at all, and
/// with which region.
/// </summary>
internal sealed class FakeScreenshotImageEditor : IScreenshotImageEditor
{
    public CaptureRect? Cropped { get; private set; }

    /// <summary>The boxes redaction was asked for, or null when it was never asked.</summary>
    public IReadOnlyList<CaptureRect>? Redacted { get; private set; }

    public byte[] Crop(byte[] png, CaptureRect region)
    {
        Cropped = region;
        return png;
    }

    public byte[] Redact(byte[] png, IReadOnlyList<CaptureRect> regions)
    {
        Redacted = regions;
        return png;
    }
}
