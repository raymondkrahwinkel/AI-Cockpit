namespace Cockpit.Core.Layout;

// The main window's remembered position, size and maximized state (#: window bounds), so the app reopens
// where and how it was last left instead of at an OS-chosen random spot/size. Persisted on close, restored
// on open (when still on a visible screen).
public sealed record WindowBounds(int X, int Y, int Width, int Height, bool IsMaximized)
{
    // Sane minimum before saved bounds are treated as usable — guards against a zero/degenerate size.
    public const int MinReasonableSize = 400;

    // Whether the stored size is large enough to restore (a collapsed/degenerate size is ignored in favour of the default).
    public bool HasUsableSize => Width >= MinReasonableSize && Height >= MinReasonableSize;
}
