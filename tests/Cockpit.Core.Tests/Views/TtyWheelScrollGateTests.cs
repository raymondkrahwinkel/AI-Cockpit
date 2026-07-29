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
    [Fact]
    public void Decide_AltScreenWithoutMouseTracking_ReturnsForwardArrowKeys()
    {
        Assert.Equal(TtyWheelScrollAction.ForwardArrowKeys, TtyWheelScrollGate.Decide(isAltScreen: true, mouseMode: 0));
    }

    [Fact]
    public void Decide_PrimaryScreen_ReturnsNativeScroll()
    {
        // #57: the primary screen has real scrollback — scroll Exclr8's buffer directly.
        Assert.Equal(TtyWheelScrollAction.NativeScroll, TtyWheelScrollGate.Decide(isAltScreen: false, mouseMode: 0));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1002)]
    [InlineData(1003)]
    public void Decide_PrimaryScreenRegardlessOfMouseMode_ReturnsNativeScroll(int mouseMode)
    {
        // Mouse-tracking modes only mean anything on the alternate screen in this codebase's usage —
        // still native-scroll the primary screen regardless of a stray MouseMode value.
        Assert.Equal(TtyWheelScrollAction.NativeScroll, TtyWheelScrollGate.Decide(isAltScreen: false, mouseMode));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1002)]
    [InlineData(1003)]
    public void Decide_AltScreenWithMouseTrackingRequested_ReturnsPassThrough(int mouseMode)
    {
        // The app asked for mouse reporting — TerminalControl's own SGR-mouse-report path already covers it.
        Assert.Equal(TtyWheelScrollAction.PassThrough, TtyWheelScrollGate.Decide(isAltScreen: true, mouseMode));
    }

    [Fact]
    public void EncodeArrowKey_ScrollUp_NormalMode_ReturnsCsiUp()
    {
        Assert.Equal(
            new byte[] { 0x1b, (byte)'[', (byte)'A' },
            TtyWheelScrollGate.EncodeArrowKey(scrollUp: true, applicationCursorKeys: false));
    }

    [Fact]
    public void EncodeArrowKey_ScrollDown_NormalMode_ReturnsCsiDown()
    {
        Assert.Equal(
            new byte[] { 0x1b, (byte)'[', (byte)'B' },
            TtyWheelScrollGate.EncodeArrowKey(scrollUp: false, applicationCursorKeys: false));
    }

    [Fact]
    public void EncodeArrowKey_ScrollUp_ApplicationCursorKeys_ReturnsSs3Up()
    {
        Assert.Equal(
            new byte[] { 0x1b, (byte)'O', (byte)'A' },
            TtyWheelScrollGate.EncodeArrowKey(scrollUp: true, applicationCursorKeys: true));
    }

    [Fact]
    public void EncodeArrowKey_ScrollDown_ApplicationCursorKeys_ReturnsSs3Down()
    {
        Assert.Equal(
            new byte[] { 0x1b, (byte)'O', (byte)'B' },
            TtyWheelScrollGate.EncodeArrowKey(scrollUp: false, applicationCursorKeys: true));
    }
}
