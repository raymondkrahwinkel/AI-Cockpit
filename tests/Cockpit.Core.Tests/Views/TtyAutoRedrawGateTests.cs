using Cockpit.App.Views;

namespace Cockpit.Core.Tests.Views;

/// <summary>
/// The pure guard behind auto-redraw-on-focus (#55): a trigger should only schedule a redraw once the
/// pty is actually running with a known terminal size.
/// </summary>
public class TtyAutoRedrawGateTests
{
    /// <summary>
    /// A redraw is only scheduled once the pty is running with a known terminal size — and not at all while a
    /// resize-settle timer is already pending for the same trigger (#58): let that own the decision, so a focus
    /// event that also caused a transient resize does not force two redraws.
    /// </summary>
    [Theory]
    [InlineData(true, 120, 40, false, true)]
    [InlineData(false, 120, 40, false, false)]
    [InlineData(true, 0, 40, false, false)]
    [InlineData(true, 120, 0, false, false)]
    [InlineData(true, -1, 40, false, false)]
    [InlineData(true, 120, 40, true, false)]
    public void ShouldScheduleRedraw_OnlyForARunningPtyOfKnownSizeWithNoSettlePending(
        bool hasPty, int columns, int rows, bool resizeSettleInFlight, bool expected)
    {
        Assert.Equal(expected, TtyAutoRedrawGate.ShouldScheduleRedraw(hasPty, columns, rows, resizeSettleInFlight));
    }
}
