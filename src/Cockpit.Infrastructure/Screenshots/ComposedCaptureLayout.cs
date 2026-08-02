using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// Places displays into one image that covers the whole desktop at a single scale (AC-326), and refuses when the
// image is not the size those displays imply.
// Two callers, for opposite reasons. Linux is *given* such an image — KWin renders every output into one
// buffer — and has to check the desktop's own display list accounts for it, because the portal says nothing
// about what went into what it hands back; a layout that does not add up is a wrong crop waiting to happen and
// is turned down instead. macOS captures each display separately and *builds* one, so it asks this where
// to draw each of them (AC-328). Windows needs neither: its blit already produces the virtual screen, monitors
// laid into it at their own pixels.
//
// The consequence worth stating on the Linux side: with one display this always works, whatever the scale,
// because one display is trivially its own bounding box. With several it holds as long as the compositor really
// does use one scale for the lot — measured on Plasma 6.7 for the single-display case only. A multi-monitor
// desktop that composes some other way ends as a refusal naming both sizes, which is an answer that can be
// acted on; guessing is not.
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

    // Both edges are scaled and only then subtracted, so displays laid edge to edge stay edge to edge in the
    // image: the right edge of one and the left edge of the next round to the same pixel. Scaling the width
    // instead would let rounding open a one-pixel seam between them, and the seam would be inside the image with
    // nothing owning it.
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
