using Cockpit.App.Services;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.ViewTests;

public sealed class ResourceMonitorSessionIdentityTests
{
    [Fact]
    public void Sample_TwoSessionsCarryingTheSameName_AreMeasuredSeparately()
    {
        // AC-1096: a title is what the operator reads and is allowed to repeat; keying the measurement on it left
        // one of the pair unmeasured and could show the other its neighbour's figure.
        ProcessRow[] rows =
        [
            new(11, 1, TimeSpan.Zero, 100),
            new(22, 1, TimeSpan.Zero, 900),
        ];
        var monitor = new ResourceMonitor(new FixedProcessTable(rows), _ => null);

        var measured = monitor.Sample([
            new SessionProcessRef("pane-a", "Session", 11),
            new SessionProcessRef("pane-b", "Session", 22),
        ]).Sessions;

        Assert.Equal(["pane-a", "pane-b"], measured.Select(session => session.PaneId));
        Assert.Equal([100L, 900L], measured.Select(session => session.MemoryBytes));
    }
}
