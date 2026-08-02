namespace Cockpit.App.Views;

// What the terminal's mouse wheel should do, since `Exclr8.Terminal.TerminalControl`'s own wheel
// handling is not right for every case Claude Code's TUI can be in (#56, #57).
//
// #56 — alternate screen, no mouse tracking: the alternate screen has zero scrollback by design (a
// full-screen TUI like Claude Code's owns its own viewport), so a wheel notch over it is a no-op unless
// the app also requested mouse tracking (DECSET 1000/1002/1003) — Claude Code's TUI does neither. This
// mirrors xterm's "alternateScroll" behaviour (DECSET 1007, which `Exclr8.Terminal` does not
// implement): translate the notch into an Up/Down arrow-key press so a full-screen app that reads arrow
// keys for its own navigation still responds to the wheel.
//
// #57 — primary/inline screen: the capture proving Claude Code's TUI renders inline (no
// `?1049/1047/47` anywhere) meant #56's `IsAltScreen` gate never fires here, which was read as
// "the wheel does nothing" on the primary screen. It doesn't need arrow-key emulation though — the
// primary screen keeps real scrollback (only the alternate screen's `ScrollbackLimit` is zeroed,
// see `TerminalBuffer.ScrollbackLimit`'s setter), and `TerminalBuffer` exposes it directly:
// `ScrollViewUp`/`ScrollViewDown` move the line-based `ScrollOffset` with no pixel/cell-
// height math needed (that lives on the private `TerminalRenderer`, which `TerminalControl`
// does not expose). Scrolling the buffer directly here — rather than leaving the event unhandled and
// counting on it reaching `TerminalControl`'s own `OnPointerWheelChanged` — makes the decision
// explicit and testable instead of depending on Avalonia's routed-event order.
//
// Alternate screen with mouse tracking requested is left alone (`TtyWheelScrollAction.PassThrough`):
// `TerminalControl`'s own SGR-mouse-report path already covers it.
public static class TtyWheelScrollGate
{
    // Lines scrolled per wheel notch on the primary screen's native scrollback (#57). Chosen to
    // match a typical terminal's per-notch scroll amount; `TerminalBuffer.ScrollViewUp`/`ScrollViewDown`
    // are line-based, so there is no cell-height/DPI conversion to get wrong here.
    public const int NativeScrollLinesPerNotch = 3;

    public static TtyWheelScrollAction Decide(bool isAltScreen, int mouseMode)
    {
        if (!isAltScreen)
        {
            return TtyWheelScrollAction.NativeScroll;
        }

        return mouseMode == 0 ? TtyWheelScrollAction.ForwardArrowKeys : TtyWheelScrollAction.PassThrough;
    }

    // The three-byte VT sequence for an Up (`scrollUp` true) or Down arrow-key press,
    // honouring DECCKM application-cursor-keys mode (SS3 `ESC O A/B`) instead of the normal CSI form
    // (`ESC [ A/B`).
    public static byte[] EncodeArrowKey(bool scrollUp, bool applicationCursorKeys)
    {
        byte introducer = applicationCursorKeys ? (byte)'O' : (byte)'[';
        byte letter = scrollUp ? (byte)'A' : (byte)'B';
        return [0x1b, introducer, letter];
    }
}
