using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// Screen capture on Windows: the whole virtual screen read in one go, with no UI of its own (AC-327). The
/// selection is the cockpit's own (AC-329); this only supplies the pixels and says where each monitor's are.
/// </summary>
/// <remarks>
/// AC-220 launched the <c>ms-screenclip:</c> overlay and then watched the clipboard for an image that was not
/// there before, because a protocol activation reports neither completion nor cancellation. Its own documentation
/// listed what that cost: a cancelled snip and a snip nobody got round to were indistinguishable, the operator's
/// clipboard was overwritten, any other image copied within the two-minute window was taken for the snip, and a
/// capture identical to what was already on the clipboard read as a cancel. None of that was a defect to fix —
/// it is what borrowing a fire-and-forget protocol costs. Reading the pixels here removes the whole class, along
/// with the timeout. Nothing here touches the clipboard any more, and since AC-341 nothing downstream does
/// either — a capture reaches a terminal session as a file the agent is handed the path to.
/// </remarks>
internal sealed class WindowsScreenshotCapture(IWindowsScreenReader screen, ILogger<WindowsScreenshotCapture> logger)
    : IScreenshotCapture
{
    public bool IsSupported => true;

    /// <summary>Nothing to ask anyone: the route this takes is part of Windows.</summary>
    public Task SupportSettled => Task.CompletedTask;

    public Task<ScreenCapture?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        // Checked before the blit rather than around it: reading the screen is one synchronous call of a few
        // milliseconds, so there is no wait to interrupt — the only useful moment to give up is before starting.
        cancellationToken.ThrowIfCancellationRequested();

        var layout = screen.ReadLayout();
        if (layout.VirtualBounds is not { Width: > 0, Height: > 0 })
        {
            throw new InvalidOperationException("Windows reports a virtual screen with no area, so there is nothing to capture.");
        }

        if (!screen.IsPerMonitorDpiAware)
        {
            // Not fatal, and not silent either. An unaware process is handed coordinates scaled to the primary
            // monitor's DPI, which is self-consistent on one screen and wrong across monitors that differ.
            logger.LogWarning(
                "This process is not per-monitor DPI aware, so a capture spanning monitors of different scales will not line up.");
        }

        var image = screen.CapturePng(layout.VirtualBounds);

        // Reading the layout and reading the pixels are two calls, and a display can be unplugged or moved
        // between them — at which point BitBlt has quietly clipped to a desktop the layout no longer describes.
        // Asking again is two system metrics; a crop against a stale layout is a screenshot of the wrong place.
        if (screen.ReadLayout().VirtualBounds != layout.VirtualBounds)
        {
            throw new InvalidOperationException("The displays changed while the screen was being read, so the capture and the layout describe different desktops.");
        }

        return Task.FromResult<ScreenCapture?>(new ScreenCapture
        {
            Image = image,
            Displays = _Place(layout),
        });
    }

    /// <summary>
    /// Each monitor's place in the image. The blit starts at the virtual screen's own corner, which is not the
    /// origin — a second monitor to the left of the primary puts it at a negative x — so the image's coordinates
    /// are the desktop's shifted by that corner, and nothing else.
    /// </summary>
    /// <remarks>
    /// No scaling enters into it. A per-monitor-aware process is given both the virtual-screen metrics and the
    /// monitor rectangles in real pixels, and the blit copies those same pixels, so a display's width on the
    /// desktop is its width in the image. That the two coordinate spaces coincide here is a fact about Windows,
    /// not an assumption the contract makes — under Wayland they do not (AC-326).
    /// </remarks>
    private static IReadOnlyList<CapturedDisplay> _Place(WindowsScreenLayout layout) =>
        layout.Displays
            .Select(display => new CapturedDisplay
            {
                DesktopBounds = display.Bounds,
                Scale = display.Scale,
                ImageBounds = new CaptureRect(
                    display.Bounds.X - layout.VirtualBounds.X,
                    display.Bounds.Y - layout.VirtualBounds.Y,
                    display.Bounds.Width,
                    display.Bounds.Height),
            })
            .ToList();
}
