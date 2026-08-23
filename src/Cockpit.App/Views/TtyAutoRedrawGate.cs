namespace Cockpit.App.Views;

// #55: whether a TTY-view redraw trigger should schedule a debounced ForceRedraw(). #58's
// TtyResizeSettleDecision is now the primary fix for the same desync; `resizeSettleInFlight`
// skips this debounce while that settle timer is pending, so the two never both fire.
public static class TtyAutoRedrawGate
{
    public static bool ShouldScheduleRedraw(bool hasPty, int columns, int rows, bool resizeSettleInFlight) =>
        hasPty && columns > 0 && rows > 0 && !resizeSettleInFlight;
}
