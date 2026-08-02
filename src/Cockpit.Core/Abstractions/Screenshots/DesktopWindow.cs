namespace Cockpit.Core.Abstractions.Screenshots;

// One window on the desktop (AC-330): what it is called, and the rectangle it occupies.
// Bounds are the window as it is drawn, not as it is managed. On Windows that distinction is the difference
// between `GetWindowRect` and `DWMWA_EXTENDED_FRAME_BOUNDS` — the former includes invisible resize
// borders, so cropping to it takes a band of whatever is behind the window along with it.
public sealed record DesktopWindow
{
    // What the window calls itself, for the surface to show while it is highlighted.
    public required string Title { get; init; }

    // Where it sits, in the desktop's own coordinates — the same space as `CapturedDisplay.DesktopBounds`.
    public required CaptureRect Bounds { get; init; }
}
