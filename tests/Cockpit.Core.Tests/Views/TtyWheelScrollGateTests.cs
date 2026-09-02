using Cockpit.App.Views;

namespace Cockpit.Core.Tests.Views;

/// <summary>
/// The pure wheel-decision logic behind #56/#57: Exclr8.Terminal's alternate screen has no scrollback, so
/// a wheel notch over a full-screen TUI that hasn't requested mouse tracking (Claude Code's TUI) is
/// forwarded as an arrow-key press instead (mirrors xterm's alternateScroll fallback); the primary/inline
/// screen Claude Code's TUI actually renders on keeps real scrollback, so it gets Exclr8's native
/// line-based scroll directly; mouse-tracking requests on the alternate screen are left untouched for
/// TerminalControl's own SGR-mouse-report path.
/// </summary>
public class TtyWheelScrollGateTests
{
    /// <summary>
    /// The alternate screen has no scrollback, so a notch over a full-screen TUI that did not ask for mouse
    /// tracking is forwarded as an arrow key (xterm's alternateScroll fallback); one that did is left to
    /// TerminalControl's own SGR-mouse-report path. The primary screen keeps real scrollback and is scrolled
    /// natively whatever a stray MouseMode value says, because those modes only mean anything on the alternate
    /// screen in this codebase's usage.
    /// </summary>
    [Theory]
    [InlineData(true, 0, TtyWheelScrollAction.ForwardArrowKeys)]
    [InlineData(true, 1000, TtyWheelScrollAction.PassThrough)]
    [InlineData(true, 1002, TtyWheelScrollAction.PassThrough)]
    [InlineData(true, 1003, TtyWheelScrollAction.PassThrough)]
    [InlineData(false, 0, TtyWheelScrollAction.NativeScroll)]
    [InlineData(false, 1000, TtyWheelScrollAction.NativeScroll)]
    [InlineData(false, 1002, TtyWheelScrollAction.NativeScroll)]
    [InlineData(false, 1003, TtyWheelScrollAction.NativeScroll)]
    public void Decide_ForwardsOnlyOnAnAltScreenThatAskedForNoMouseTracking(
        bool isAltScreen, int mouseMode, TtyWheelScrollAction expected)
    {
        Assert.Equal(expected, TtyWheelScrollGate.Decide(isAltScreen, mouseMode));
    }

    // CSI in normal mode, SS3 once the app asked for application cursor keys; A up, B down.
    [Theory]
    [InlineData(true, false, '[', 'A')]
    [InlineData(false, false, '[', 'B')]
    [InlineData(true, true, 'O', 'A')]
    [InlineData(false, true, 'O', 'B')]
    public void EncodeArrowKey_UsesTheIntroducerTheModeAsksFor(
        bool scrollUp, bool applicationCursorKeys, char introducer, char direction)
    {
        Assert.Equal(
            new byte[] { 0x1b, (byte)introducer, (byte)direction },
            TtyWheelScrollGate.EncodeArrowKey(scrollUp, applicationCursorKeys));
    }
}
