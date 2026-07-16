namespace Cockpit.App.Views;

/// <summary>
/// Whether a TTY-view auto-redraw trigger (<c>TtyView</c>'s <c>TerminalControl</c> regaining focus,
/// the owning window activating, or the pane becoming visible/attached again — #55) should schedule a
/// debounced <c>ForceRedraw()</c>. Extracted out of the view's code-behind so the guard is unit-testable
/// without an Avalonia UI thread, same reasoning as <c>PushToTalkKeyGate</c>: there is nothing to redraw
/// before the pty has actually spawned with a known size, and scheduling then would race the initial
/// spawn/resize-settle path instead of fixing the reported render-desync bug.
/// </summary>
/// <remarks>
/// #58 made the resize-settle path (<c>TtyResizeSettleDecision</c>) the primary, deterministic fix for
/// the same render desync this gate's trigger was originally a heuristic vangnet for — a focus/activation
/// event that also caused a transient resize now gets its own <c>ForceRedraw()</c> decision from the
/// settle timer once it fires. <paramref name="resizeSettleInFlight"/> lets a caller skip scheduling this
/// debounce while that settle timer is still pending, so the two mechanisms do not both fire a redraw for
/// the same underlying trigger; the settle timer is what decides once it runs. This gate still fires
/// normally for a pure focus/activation event with no resize transient at all (#55's remaining case).
/// </remarks>
public static class TtyAutoRedrawGate
{
    public static bool ShouldScheduleRedraw(bool hasPty, int columns, int rows, bool resizeSettleInFlight) =>
        hasPty && columns > 0 && rows > 0 && !resizeSettleInFlight;
}
