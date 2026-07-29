using Cockpit.App.Views;

namespace Cockpit.Core.Tests.Views;

/// <summary>
/// The pure guard behind auto-redraw-on-focus (#55): a trigger should only schedule a redraw once the
/// pty is actually running with a known terminal size.
/// </summary>
public class TtyAutoRedrawGateTests
{
    [Fact]
    public void ShouldScheduleRedraw_PtyRunningWithKnownSize_ReturnsTrue()
    {
        Assert.True(
            TtyAutoRedrawGate.ShouldScheduleRedraw(hasPty: true, columns: 120, rows: 40, resizeSettleInFlight: false));
    }

    [Fact]
    public void ShouldScheduleRedraw_NoPty_ReturnsFalse()
    {
        Assert.False(
            TtyAutoRedrawGate.ShouldScheduleRedraw(hasPty: false, columns: 120, rows: 40, resizeSettleInFlight: false));
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(120, 0)]
    [InlineData(-1, 40)]
    public void ShouldScheduleRedraw_UnknownSize_ReturnsFalse(int columns, int rows)
    {
        Assert.False(
            TtyAutoRedrawGate.ShouldScheduleRedraw(hasPty: true, columns, rows, resizeSettleInFlight: false));
    }

    [Fact]
    public void ShouldScheduleRedraw_ResizeSettleInFlight_ReturnsFalse()
    {
        // #58: a resize-settle timer is already pending for the same trigger — let it own the redraw
        // decision instead of also firing this debounce, so a focus event that also caused a transient
        // resize does not force two redraws.
        Assert.False(
            TtyAutoRedrawGate.ShouldScheduleRedraw(hasPty: true, columns: 120, rows: 40, resizeSettleInFlight: true));
    }
}
