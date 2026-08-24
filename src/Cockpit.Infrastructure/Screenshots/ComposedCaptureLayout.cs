using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// AC-1013 (AC-326): Places displays into one image at a single scale, refusing when the image size does not
// match. Linux verifies a given composited image against the portal's own display list (no cross-check
// otherwise); macOS builds one from separate captures (AC-328). Trimmed: multi-monitor scale caveat, measured only on Plasma 6.7.
internal static class ComposedCaptureLayout
{
    // The displays placed into the image, or `null` when the image is not the size those
    // displays imply.
    public static IReadOnlyList<CapturedDisplay>? TryCompose(IReadOnlyList<DesktopDisplay> displays, int imageWidth, int imageHeight)
    {
        if (displays.Count == 0 || displays.Any(display => display.Bounds is not { Width: > 0, Height: > 0 }))
        {
            return null;
        }

        var desktop = _BoundingBox(displays);
        var ratio = imageWidth / (double)desktop.Width;

        // A desktop never renders a display below its own resolution, so an image smaller than the layout is not
        // a scale — it is a layout describing a different desktop than the one that was captured.
        if (ratio < 1)
        {
            return null;
        }

        // One pixel of slack, and no more: the compositor rounds a fractional scale to whole pixels, and an odd
        // desktop height at 150% cannot come out exact. Anything beyond that is two different desktops.
        if (Math.Abs(desktop.Height * ratio - imageHeight) > 1)
        {
            return null;
        }

        return displays
            .Select(display => new CapturedDisplay
            {
                DesktopBounds = display.Bounds,
                Scale = display.Scale,
                ImageBounds = _Place(display.Bounds, desktop, imageWidth, imageHeight),
            })
            .ToList();
    }

    private static CaptureRect _BoundingBox(IReadOnlyList<DesktopDisplay> displays)
    {
        var left = displays.Min(display => display.Bounds.X);
        var top = displays.Min(display => display.Bounds.Y);
        var right = displays.Max(display => display.Bounds.Right);
        var bottom = displays.Max(display => display.Bounds.Bottom);

        return new CaptureRect(left, top, right - left, bottom - top);
    }

    // AC-1013: Both edges are scaled, then subtracted, so adjacent displays round to the same pixel and stay
    // edge to edge; scaling the width instead would open an unowned one-pixel seam.
    private static CaptureRect _Place(CaptureRect bounds, CaptureRect desktop, int imageWidth, int imageHeight)
    {
        var left = _EdgeOf(bounds.X - desktop.X, imageWidth, desktop.Width);
        var top = _EdgeOf(bounds.Y - desktop.Y, imageHeight, desktop.Height);
        var right = _EdgeOf(bounds.Right - desktop.X, imageWidth, desktop.Width);
        var bottom = _EdgeOf(bounds.Bottom - desktop.Y, imageHeight, desktop.Height);

        return new CaptureRect(left, top, right - left, bottom - top);
    }

    // The same convention CapturedDisplay maps points with: desktop position n starts at image pixel ceil(n·s),
    // because position n covers pixels [n·s, (n+1)·s) and the first of those is where a crop begins.
    private static int _EdgeOf(int position, int imageExtent, int desktopExtent) =>
        (int)Math.Ceiling(position * (double)imageExtent / desktopExtent);
}
