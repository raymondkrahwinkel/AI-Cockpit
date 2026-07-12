namespace Cockpit.Core.Layout;

/// <summary>
/// The main window's remembered position, size and maximized state (#: window bounds), so the app reopens
/// where and how it was last left instead of at an OS-chosen random spot/size. Persisted on close, restored
/// on open (when still on a visible screen).
/// </summary>
public sealed record WindowBounds(int X, int Y, int Width, int Height, bool IsMaximized)
{
    /// <summary>Sane minimum before saved bounds are treated as usable — guards against a zero/degenerate size.</summary>
    public const int MinReasonableSize = 400;

    /// <summary>Whether the stored size is large enough to restore (a collapsed/degenerate size is ignored in favour of the default).</summary>
    public bool HasUsableSize => Width >= MinReasonableSize && Height >= MinReasonableSize;
}
