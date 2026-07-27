using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// Records what was asked of the editor and hands the bytes straight back (AC-329). The crop and the burning-in
/// are Skia's and are tested where they live; what these tests care about is whether a coordinator asked for them
/// at all, and with what.
/// </summary>
internal sealed class FakeScreenshotImageEditor : IScreenshotImageEditor
{
    public CaptureRect? Cropped { get; private set; }

    /// <summary>The marks burning-in was asked for, or null when it was never asked.</summary>
    public IReadOnlyList<Mark>? Burnt { get; private set; }

    public byte[] Crop(byte[] png, CaptureRect region)
    {
        Cropped = region;
        return png;
    }

    public byte[] Burn(byte[] png, IReadOnlyList<Mark> marks)
    {
        Burnt = marks;
        return png;
    }
}
