namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>One window on the desktop (AC-330): what it is called, and the rectangle it occupies.</summary>
/// <remarks>
/// Bounds are the window as it is drawn, not as it is managed. On Windows that distinction is the difference
/// between <c>GetWindowRect</c> and <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> — the former includes invisible resize
/// borders, so cropping to it takes a band of whatever is behind the window along with it.
/// </remarks>
public sealed record DesktopWindow
{
    /// <summary>What the window calls itself, for the surface to show while it is highlighted.</summary>
    public required string Title { get; init; }

    /// <summary>Where it sits, in the desktop's own coordinates — the same space as <see cref="CapturedDisplay.DesktopBounds"/>.</summary>
    public required CaptureRect Bounds { get; init; }
}
