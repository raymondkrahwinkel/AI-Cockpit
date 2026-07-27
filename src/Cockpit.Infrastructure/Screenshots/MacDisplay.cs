using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.Infrastructure.Screenshots;

/// <summary>
/// One macOS display as both halves of the problem see it (AC-328): where it sits on the desktop in points,
/// which is the only space <c>CGDisplayBounds</c> speaks, and how many pixels it actually has.
/// </summary>
/// <remarks>
/// The two are not the same number and the gap is the whole reason this type exists. A Retina panel reports
/// 1710 × 1112 points and captures at 3420 × 2224, and macOS — unlike a Linux compositor — does not force one
/// factor across displays, so a Retina laptop beside an ordinary monitor has two of them at once.
/// </remarks>
internal sealed record MacDisplay
{
    /// <summary>
    /// The display's place in <c>screencapture -D</c>'s own numbering, which is one-based and follows
    /// <c>CGGetActiveDisplayList</c>'s order.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>Where this display sits on the desktop, in points — <c>CGDisplayBounds</c>' space.</summary>
    public required CaptureRect Bounds { get; init; }

    /// <summary>How many pixels wide the capture of this display comes back.</summary>
    public required int PixelWidth { get; init; }

    /// <summary>
    /// How many pixels tall it comes back. Asked for separately rather than derived from
    /// <see cref="Scale"/>, so that what the capture actually contains can be checked against what it was
    /// expected to contain instead of being assumed square.
    /// </summary>
    public required int PixelHeight { get; init; }

    /// <summary>What this display's points are worth in pixels: 2 on a Retina panel, 1 on an ordinary monitor.</summary>
    public double Scale => Bounds.Width > 0 ? PixelWidth / (double)Bounds.Width : 1d;
}
