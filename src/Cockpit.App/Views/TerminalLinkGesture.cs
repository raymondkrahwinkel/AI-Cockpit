namespace Cockpit.App.Views;

// AC-2: which pointer press over a terminal opens a link (one opener, one gesture, every mouse mode).
// Kept free of Avalonia types so the rule is unit-testable without a terminal, a pty or a platform.
internal static class TerminalLinkGesture
{
    // AC-560: click count must be 1, else a Ctrl+double-click opens the URL twice.
    // TerminalControl.ActivateLink gates on the same `_clickCount == 1` so both openers agree.
    public static bool Opens(bool controlHeld, bool leftButtonPressed, int clickCount) =>
        controlHeld && leftButtonPressed && clickCount == 1;
}
