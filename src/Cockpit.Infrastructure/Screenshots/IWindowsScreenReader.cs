using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// What Windows can be asked about its screens, and for their pixels (AC-327). Split from the capture so the
/// part with decisions in it — which displays go where in the image, what a capture of nothing means — is
/// assertable without a desktop under it.
/// </summary>
internal interface IWindowsScreenReader
{
    /// <summary>
    /// Whether the process sees screen coordinates in real pixels rather than the scaled ones Windows hands an
    /// unaware process. Not a reason to refuse — a single monitor captures correctly either way, only at its
    /// scaled resolution — but it is what mixed-DPI multi-monitor gets wrong, so it is said out loud.
    /// </summary>
    bool IsPerMonitorDpiAware { get; }

    /// <summary>
    /// The virtual screen and the displays that make it up, read together — so those two cannot disagree with
    /// each other. They can still disagree with a capture taken after them, which is the caller's to notice.
    /// </summary>
    WindowsScreenLayout ReadLayout();

    /// <summary>
    /// The given rectangle of the virtual screen, as PNG bytes.
    /// </summary>
    byte[] CapturePng(CaptureRect bounds);
}
