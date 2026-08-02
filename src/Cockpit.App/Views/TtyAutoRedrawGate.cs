namespace Cockpit.App.Views;

// Whether a TTY-view auto-redraw trigger (`TtyView`'s `TerminalControl` regaining focus,
// the owning window activating, or the pane becoming visible/attached again — #55) should schedule a
// debounced `ForceRedraw()`. Extracted out of the view's code-behind so the guard is unit-testable
// without an Avalonia UI thread, same reasoning as `PushToTalkKeyGate`: there is nothing to redraw
// before the pty has actually spawned with a known size, and scheduling then would race the initial
// spawn/resize-settle path instead of fixing the reported render-desync bug.
// #58 made the resize-settle path (`TtyResizeSettleDecision`) the primary, deterministic fix for
// the same render desync this gate's trigger was originally a heuristic vangnet for — a focus/activation
// event that also caused a transient resize now gets its own `ForceRedraw()` decision from the
// settle timer once it fires. `resizeSettleInFlight` lets a caller skip scheduling this
// debounce while that settle timer is still pending, so the two mechanisms do not both fire a redraw for
// the same underlying trigger; the settle timer is what decides once it runs. This gate still fires
// normally for a pure focus/activation event with no resize transient at all (#55's remaining case).
public static class TtyAutoRedrawGate
{
    public static bool ShouldScheduleRedraw(bool hasPty, int columns, int rows, bool resizeSettleInFlight) =>
        hasPty && columns > 0 && rows > 0 && !resizeSettleInFlight;
}
