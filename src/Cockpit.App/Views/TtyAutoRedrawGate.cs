namespace Cockpit.App.Views;

/// <summary>
/// Whether a TTY-view auto-redraw trigger (<c>ClaudeTtyView</c>'s <c>TerminalControl</c> regaining focus,
/// the owning window activating, or the pane becoming visible/attached again — #55) should schedule a
/// debounced <c>ForceRedraw()</c>. Extracted out of the view's code-behind so the guard is unit-testable
/// without an Avalonia UI thread, same reasoning as <c>PushToTalkKeyGate</c>: there is nothing to redraw
/// before the pty has actually spawned with a known size, and scheduling then would race the initial
/// spawn/resize-settle path instead of fixing the reported render-desync bug.
/// </summary>
public static class TtyAutoRedrawGate
{
    public static bool ShouldScheduleRedraw(bool hasPty, int columns, int rows) =>
        hasPty && columns > 0 && rows > 0;
}
