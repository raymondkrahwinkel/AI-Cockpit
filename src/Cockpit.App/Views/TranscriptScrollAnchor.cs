namespace Cockpit.App.Views;

/// <summary>
/// Pure geometry for the transcript's stick-to-bottom auto-scroll: decides whether the viewport is
/// parked at the bottom (so new content should keep following) versus scrolled up to read history
/// (so it should stay put). Kept free of Avalonia types so the decision is unit-testable; the view's
/// code-behind feeds it the live ScrollViewer offset/extent/viewport.
/// </summary>
internal static class TranscriptScrollAnchor
{
    /// <summary>
    /// True when the viewport sits at (or within <paramref name="tolerance"/> of) the bottom of the
    /// content, or when the content is shorter than the viewport (nothing to scroll). A small
    /// tolerance absorbs sub-pixel layout rounding so a genuine bottom still counts as the bottom.
    /// </summary>
    public static bool IsAtBottom(double offsetY, double extentHeight, double viewportHeight, double tolerance = 2.0)
    {
        var maxOffset = extentHeight - viewportHeight;
        if (maxOffset <= 0)
        {
            return true;
        }

        return offsetY >= maxOffset - tolerance;
    }
}
