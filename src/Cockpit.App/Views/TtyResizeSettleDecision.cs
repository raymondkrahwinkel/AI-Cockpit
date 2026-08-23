namespace Cockpit.App.Views;

// #58: deterministic fix for the stacked-at-top desync (Cockpit-TTY-Glitch-RootCause-2026-07-11.md)
// — a Wayland/KDE focus event can net back to the same grid size, and an unchanged winsize never
// raises SIGWINCH. Comparing against the size last actually sent to the pty catches that and forces a redraw.
public static class TtyResizeSettleDecision
{
    public static TtyResizeSettleAction Decide(
        int lastSentColumns, int lastSentRows, int currentColumns, int currentRows) =>
        currentColumns == lastSentColumns && currentRows == lastSentRows
            ? TtyResizeSettleAction.Redraw
            : TtyResizeSettleAction.Resize;
}
