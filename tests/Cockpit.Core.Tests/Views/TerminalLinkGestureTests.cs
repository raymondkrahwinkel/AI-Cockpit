using Cockpit.App.Views;

namespace Cockpit.Core.Tests.Views;

public class TerminalLinkGestureTests
{
    [Fact]
    public void CtrlPlusASingleLeftPress_Opens() =>
        Assert.True(TerminalLinkGesture.Opens(controlHeld: true, leftButtonPressed: true, clickCount: 1));

    /// <summary>
    /// AC-560. A double-click delivers two presses over the same link — the first with ClickCount 1, the second
    /// with 2 — and without this the second one opened the URL a second time.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void TheLaterPressesOfAMultiClick_DoNotOpenAgain(int clickCount) =>
        Assert.False(TerminalLinkGesture.Opens(controlHeld: true, leftButtonPressed: true, clickCount));

    [Fact]
    public void WithoutCtrl_NothingOpens_SoThePressReachesThePty() =>
        Assert.False(TerminalLinkGesture.Opens(controlHeld: false, leftButtonPressed: true, clickCount: 1));

    [Fact]
    public void ANonLeftButton_DoesNotOpen() =>
        Assert.False(TerminalLinkGesture.Opens(controlHeld: true, leftButtonPressed: false, clickCount: 1));
}
