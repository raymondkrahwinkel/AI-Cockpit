namespace Cockpit.App.Views;

/// <summary>
/// Which pointer press over a terminal opens a link (AC-2: one opener, one gesture, in every mouse mode).
/// Kept free of Avalonia types so the rule is unit-testable without a terminal, a pty or a platform.
/// </summary>
internal static class TerminalLinkGesture
{
    /// <summary>
    /// Ctrl held, left button, and the <em>first</em> press of the sequence. The click count is what AC-560 was
    /// about: without it a Ctrl+double-click delivers two presses over the same link and opens the URL twice.
    /// <c>TerminalControl.ActivateLink</c> gates its own path on <c>_clickCount == 1</c> for the same reason; this
    /// is that rule on Cockpit's side of the gesture, so both openers agree on what a click is.
    /// </summary>
    public static bool Opens(bool controlHeld, bool leftButtonPressed, int clickCount) =>
        controlHeld && leftButtonPressed && clickCount == 1;
}
