using Cockpit.App.Views;
using FluentAssertions;

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
        TtyAutoRedrawGate.ShouldScheduleRedraw(hasPty: true, columns: 120, rows: 40).Should().BeTrue();
    }

    [Fact]
    public void ShouldScheduleRedraw_NoPty_ReturnsFalse()
    {
        TtyAutoRedrawGate.ShouldScheduleRedraw(hasPty: false, columns: 120, rows: 40).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(120, 0)]
    [InlineData(-1, 40)]
    public void ShouldScheduleRedraw_UnknownSize_ReturnsFalse(int columns, int rows)
    {
        TtyAutoRedrawGate.ShouldScheduleRedraw(hasPty: true, columns, rows).Should().BeFalse();
    }
}
