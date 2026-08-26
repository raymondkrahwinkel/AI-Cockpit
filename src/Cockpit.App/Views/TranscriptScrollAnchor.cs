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

    // AC-1113: a ScrollChanged carrying only an offset delta is the pass the follow's own viewport move drove,
    // not new content. Avalonia raises ScrollChanged from LayoutUpdated, i.e. after the layout pass has ended,
    // so following such a change queues yet another pass and Avalonia cuts the frame at 153 of them.
    public static bool IsOwnCorrection(double extentDelta, double viewportDelta)
        => Math.Abs(extentDelta) < 0.5 && Math.Abs(viewportDelta) < 0.5;

    // A real change may always follow; its own correction may once — the two-step follow's second half, no more.
    // ponytail: that one correction is the ceiling, leaving a still-short estimate a few pixels off the tail;
    // raise it to a small budget if the residue ever shows on screen.
    public static bool MayFollow(bool ownCorrection, bool alreadyCorrected) => !ownCorrection || !alreadyCorrected;

    // True when `target` is where the viewport already sits. Writing it anyway would invalidate layout for a
    // fraction of a pixel, and every such write is another pass towards Avalonia's cut-off.
    public static bool IsSettled(double currentOffsetY, double targetOffsetY, double tolerance = 0.5)
        => Math.Abs(targetOffsetY - currentOffsetY) < tolerance;
}
