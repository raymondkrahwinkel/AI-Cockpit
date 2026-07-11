namespace Cockpit.App.Views;

/// <summary>
/// Whether the terminal's mouse wheel should be forwarded to the pty as an arrow-key press instead of
/// letting <c>Exclr8.Terminal.TerminalControl</c> handle it itself (#56). The alternate screen has zero
/// scrollback by design (a full-screen TUI like Claude Code's owns its own viewport), so a wheel notch
/// over it is a no-op unless the app also requested mouse tracking (DECSET 1000/1002/1003) — Claude
/// Code's TUI does neither. This mirrors xterm's "alternateScroll" behaviour (DECSET 1007, which
/// <c>Exclr8.Terminal</c> does not implement): translate the notch into an Up/Down arrow-key press so a
/// full-screen app that reads arrow keys for its own navigation still responds to the wheel. Only kicks
/// in when <c>TerminalControl.OnPointerWheelChanged</c>'s own SGR-mouse-report path — active whenever
/// <c>IsAltScreen &amp;&amp; MouseMode &gt; 0</c> — would not otherwise handle the event.
/// </summary>
public static class TtyWheelScrollGate
{
    public static bool ShouldForwardAsArrowKeys(bool isAltScreen, int mouseMode) => isAltScreen && mouseMode == 0;

    /// <summary>
    /// The three-byte VT sequence for an Up (<paramref name="scrollUp"/> true) or Down arrow-key press,
    /// honouring DECCKM application-cursor-keys mode (SS3 <c>ESC O A/B</c>) instead of the normal CSI form
    /// (<c>ESC [ A/B</c>).
    /// </summary>
    public static byte[] EncodeArrowKey(bool scrollUp, bool applicationCursorKeys)
    {
        byte introducer = applicationCursorKeys ? (byte)'O' : (byte)'[';
        byte letter = scrollUp ? (byte)'A' : (byte)'B';
        return [0x1b, introducer, letter];
    }
}
