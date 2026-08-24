namespace Cockpit.Core.Abstractions.Screenshots;

// AC-330: one window on the desktop — its title and the rectangle it occupies. Bounds are the window as
// drawn, not as managed: `GetWindowRect` includes invisible resize borders, unlike `DWMWA_EXTENDED_FRAME_BOUNDS`.
public sealed record DesktopWindow
{
    // What the window calls itself, for the surface to show while it is highlighted.
    public required string Title { get; init; }

    // Where it sits, in the desktop's own coordinates — the same space as `CapturedDisplay.DesktopBounds`.
    public required CaptureRect Bounds { get; init; }
}
