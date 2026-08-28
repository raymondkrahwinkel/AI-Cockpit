namespace Cockpit.App.Views;

// Pure geometry for the transcript's stick-to-bottom auto-scroll: parked at the bottom keeps
// following new content, scrolled up stays put. Kept free of Avalonia types so it is
// unit-testable; the view's code-behind feeds it the live ScrollViewer offset/extent/viewport.
internal static class TranscriptScrollAnchor
{
    // True at (or within `tolerance` of) the bottom, or when content is shorter than the
    // viewport. The tolerance absorbs sub-pixel rounding so a genuine bottom still counts.
    public static bool IsAtBottom(double offsetY, double extentHeight, double viewportHeight, double tolerance = 2.0)
    {
        var maxOffset = extentHeight - viewportHeight;
        if (maxOffset <= 0)
        {
            return true;
        }

        return offsetY >= maxOffset - tolerance;
    }

    // True when `target` is where the viewport already sits. Writing it anyway would invalidate layout for a
    // fraction of a pixel, and every such write is another pass towards Avalonia's cut-off.
    public static bool IsSettled(double currentOffsetY, double targetOffsetY, double tolerance = 0.5)
        => Math.Abs(targetOffsetY - currentOffsetY) < tolerance;
}
