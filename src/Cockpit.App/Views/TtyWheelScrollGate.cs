namespace Cockpit.App.Views;

/// <summary>
/// What the terminal's mouse wheel should do, since <c>Exclr8.Terminal.TerminalControl</c>'s own wheel
/// handling is not right for every case Claude Code's TUI can be in (#56, #57).
/// </summary>
/// <remarks>
/// <para>
/// #56 — alternate screen, no mouse tracking: the alternate screen has zero scrollback by design (a
/// full-screen TUI like Claude Code's owns its own viewport), so a wheel notch over it is a no-op unless
/// the app also requested mouse tracking (DECSET 1000/1002/1003) — Claude Code's TUI does neither. This
/// mirrors xterm's "alternateScroll" behaviour (DECSET 1007, which <c>Exclr8.Terminal</c> does not
/// implement): translate the notch into an Up/Down arrow-key press so a full-screen app that reads arrow
/// keys for its own navigation still responds to the wheel.
/// </para>
/// <para>
/// #57 — primary/inline screen: the capture proving Claude Code's TUI renders inline (no
/// <c>?1049/1047/47</c> anywhere) meant #56's <c>IsAltScreen</c> gate never fires here, which was read as
/// "the wheel does nothing" on the primary screen. It doesn't need arrow-key emulation though — the
/// primary screen keeps real scrollback (only the alternate screen's <c>ScrollbackLimit</c> is zeroed,
/// see <c>TerminalBuffer.ScrollbackLimit</c>'s setter), and <c>TerminalBuffer</c> exposes it directly:
/// <c>ScrollViewUp</c>/<c>ScrollViewDown</c> move the line-based <c>ScrollOffset</c> with no pixel/cell-
/// height math needed (that lives on the private <c>TerminalRenderer</c>, which <c>TerminalControl</c>
/// does not expose). Scrolling the buffer directly here — rather than leaving the event unhandled and
/// counting on it reaching <c>TerminalControl</c>'s own <c>OnPointerWheelChanged</c> — makes the decision
/// explicit and testable instead of depending on Avalonia's routed-event order.
/// </para>
/// <para>
/// Alternate screen with mouse tracking requested is left alone (<see cref="TtyWheelScrollAction.PassThrough"/>):
/// <c>TerminalControl</c>'s own SGR-mouse-report path already covers it.
/// </para>
/// </remarks>
public static class TtyWheelScrollGate
{
    /// <summary>Lines scrolled per wheel notch on the primary screen's native scrollback (#57). Chosen to
    /// match a typical terminal's per-notch scroll amount; <c>TerminalBuffer.ScrollViewUp</c>/<c>ScrollViewDown</c>
    /// are line-based, so there is no cell-height/DPI conversion to get wrong here.</summary>
    public const int NativeScrollLinesPerNotch = 3;

    public static TtyWheelScrollAction Decide(bool isAltScreen, int mouseMode)
    {
        if (!isAltScreen)
        {
            return TtyWheelScrollAction.NativeScroll;
        }

        return mouseMode == 0 ? TtyWheelScrollAction.ForwardArrowKeys : TtyWheelScrollAction.PassThrough;
    }

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
