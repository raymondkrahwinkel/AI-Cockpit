using Cockpit.App.Views;

namespace Cockpit.Core.Tests.Views;

public class TerminalLinkGestureTests
{
    /// <summary>
    /// Ctrl and a single left press opens; everything else leaves the press for the pty. AC-560 is the middle
    /// pair: a double-click delivers two presses over the same link — the first with ClickCount 1, the second
    /// with 2 — and without the count check the second one opened the URL again.
    /// </summary>
    [Theory]
    [InlineData(true, true, 1, true)]
    [InlineData(true, true, 2, false)]
    [InlineData(true, true, 3, false)]
    [InlineData(false, true, 1, false)]
    [InlineData(true, false, 1, false)]
    public void Opens_OnlyForCtrlAndASingleLeftPress(bool controlHeld, bool leftButtonPressed, int clickCount, bool expected) =>
        Assert.Equal(expected, TerminalLinkGesture.Opens(controlHeld, leftButtonPressed, clickCount));
}
