using Cockpit.App.Views;
using FluentAssertions;

namespace Cockpit.Core.Tests.Views;

/// <summary>
/// The pure alt-screen-wheel-to-arrow-keys logic behind #56: Exclr8.Terminal's alternate screen has no
/// scrollback, so a wheel notch over a full-screen TUI that hasn't requested mouse tracking (Claude
/// Code's TUI) otherwise does nothing — this mirrors xterm's alternateScroll fallback instead.
/// </summary>
public class TtyWheelScrollGateTests
{
    [Fact]
    public void ShouldForwardAsArrowKeys_AltScreenWithoutMouseTracking_ReturnsTrue()
    {
        TtyWheelScrollGate.ShouldForwardAsArrowKeys(isAltScreen: true, mouseMode: 0).Should().BeTrue();
    }

    [Fact]
    public void ShouldForwardAsArrowKeys_PrimaryScreen_ReturnsFalse()
    {
        // The primary screen has real scrollback — TerminalControl's own pixel-scroll handling applies.
        TtyWheelScrollGate.ShouldForwardAsArrowKeys(isAltScreen: false, mouseMode: 0).Should().BeFalse();
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1002)]
    [InlineData(1003)]
    public void ShouldForwardAsArrowKeys_AltScreenWithMouseTrackingRequested_ReturnsFalse(int mouseMode)
    {
        // The app asked for mouse reporting — TerminalControl's own SGR-mouse-report path already covers it.
        TtyWheelScrollGate.ShouldForwardAsArrowKeys(isAltScreen: true, mouseMode).Should().BeFalse();
    }

    [Fact]
    public void EncodeArrowKey_ScrollUp_NormalMode_ReturnsCsiUp()
    {
        TtyWheelScrollGate.EncodeArrowKey(scrollUp: true, applicationCursorKeys: false)
            .Should().Equal(0x1b, (byte)'[', (byte)'A');
    }

    [Fact]
    public void EncodeArrowKey_ScrollDown_NormalMode_ReturnsCsiDown()
    {
        TtyWheelScrollGate.EncodeArrowKey(scrollUp: false, applicationCursorKeys: false)
            .Should().Equal(0x1b, (byte)'[', (byte)'B');
    }

    [Fact]
    public void EncodeArrowKey_ScrollUp_ApplicationCursorKeys_ReturnsSs3Up()
    {
        TtyWheelScrollGate.EncodeArrowKey(scrollUp: true, applicationCursorKeys: true)
            .Should().Equal(0x1b, (byte)'O', (byte)'A');
    }

    [Fact]
    public void EncodeArrowKey_ScrollDown_ApplicationCursorKeys_ReturnsSs3Down()
    {
        TtyWheelScrollGate.EncodeArrowKey(scrollUp: false, applicationCursorKeys: true)
            .Should().Equal(0x1b, (byte)'O', (byte)'B');
    }
}
