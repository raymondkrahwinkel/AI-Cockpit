using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

// One macOS display as both halves of the problem see it (AC-328): where it sits on the desktop in points,
// which is the only space `CGDisplayBounds` speaks, and how many pixels it actually has.
// The two are not the same number and the gap is the whole reason this type exists. A Retina panel reports
// 1710 × 1112 points and captures at 3420 × 2224, and macOS — unlike a Linux compositor — does not force one
// factor across displays, so a Retina laptop beside an ordinary monitor has two of them at once.
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
