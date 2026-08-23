namespace Cockpit.App.Views;

// #56/#57: what the mouse wheel should do, since Exclr8.Terminal's own handling is wrong for
// Claude Code's TUI. #56: alt screen requests no mouse tracking, so a notch becomes an arrow key
// (mirrors xterm alternateScroll). #57: primary screen scrolls TerminalBuffer directly, deterministically.
public static class TtyWheelScrollGate
{
    // #57: lines per wheel notch on the primary screen's native scrollback, matching a typical
    // terminal — ScrollViewUp/Down are line-based, so no cell-height/DPI conversion is needed.
    public const int NativeScrollLinesPerNotch = 3;

    public static TtyWheelScrollAction Decide(bool isAltScreen, int mouseMode)
    {
        if (!isAltScreen)
        {
            return TtyWheelScrollAction.NativeScroll;
        }

        return mouseMode == 0 ? TtyWheelScrollAction.ForwardArrowKeys : TtyWheelScrollAction.PassThrough;
    }

    // Three-byte VT sequence for an Up/Down arrow-key press, honouring DECCKM application-cursor-
    // keys mode (SS3 `ESC O A/B`) instead of the normal CSI form (`ESC [ A/B`).
    public static byte[] EncodeArrowKey(bool scrollUp, bool applicationCursorKeys)
    {
        byte introducer = applicationCursorKeys ? (byte)'O' : (byte)'[';
        byte letter = scrollUp ? (byte)'A' : (byte)'B';
        return [0x1b, introducer, letter];
    }
}
