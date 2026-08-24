using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// AC-1013: Windows capture reads the whole virtual screen in one go, no UI of its own (AC-327); selection is
// the cockpit's own (AC-329). Replaces AC-220's `ms-screenclip:` clipboard-watch approach, whose fire-and-forget
// protocol made cancel/timeout/overwrite indistinguishable; reading pixels directly removes that class of bug.
internal sealed class WindowsScreenshotCapture(IWindowsScreenReader screen, ILogger<WindowsScreenshotCapture> logger)
    : IScreenshotCapture
{
    public bool IsSupported => true;

    // Nothing to ask anyone: the route this takes is part of Windows.
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

    // AC-1013: each monitor's place in the image, shifted by the virtual screen's own (possibly negative)
    // corner and otherwise unscaled — a per-monitor-aware process gets real pixels both for the blit and the
    // monitor rects, so image and desktop coordinates coincide. That's a Windows fact, not true under Wayland (AC-326).
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
