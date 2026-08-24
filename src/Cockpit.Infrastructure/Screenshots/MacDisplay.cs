using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// AC-1013 (AC-328): One macOS display in both points (`CGDisplayBounds`) and pixels, because macOS — unlike
// a Linux compositor — doesn't force one scale factor across displays (Retina laptop beside an ordinary monitor).
// Trimmed: the concrete 1710x1112 vs 3420x2224 Retina point/pixel example.
internal sealed record MacDisplay
{
    // The display's place in `screencapture -D`'s own numbering, which is one-based and follows
    // `CGGetActiveDisplayList`'s order.
    public required int Index { get; init; }

    // Where this display sits on the desktop, in points — `CGDisplayBounds`' space.
    public required CaptureRect Bounds { get; init; }

    // How many pixels wide the capture of this display comes back.
    public required int PixelWidth { get; init; }

    // How many pixels tall it comes back. Asked for separately rather than derived from
    // `Scale`, so that what the capture actually contains can be checked against what it was
    // expected to contain instead of being assumed square.
    public required int PixelHeight { get; init; }

    // What this display's points are worth in pixels: 2 on a Retina panel, 1 on an ordinary monitor.
    public double Scale => Bounds.Width > 0 ? PixelWidth / (double)Bounds.Width : 1d;
}
