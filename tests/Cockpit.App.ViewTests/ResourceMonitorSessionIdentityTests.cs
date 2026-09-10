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

    [Fact]
    public void Sample_WhenOneSessionStartsAProcess_TheOtherSessionsFiguresDoNotMove()
    {
        // AC-1310: membership is per root, so two sessions cannot claim the same tree — which is what lets a
        // status be derived from these figures at all. If B's work could leak into A's count, A would read as
        // working whenever its neighbour did.
        var table = new FixedProcessTable([new ProcessRow(11, 1, TimeSpan.Zero, 100), new ProcessRow(22, 1, TimeSpan.Zero, 900)]);
        var monitor = new ResourceMonitor(table, _ => null);
        SessionProcessRef[] sessions = [new("pane-a", "A", 11), new("pane-b", "B", 22)];

        monitor.Sample(sessions);
        table.Rows = [.. table.Rows, new ProcessRow(33, 22, TimeSpan.Zero, 500)];
        var measured = monitor.Sample(sessions);

        var a = measured.Sessions.Single(session => session.PaneId == "pane-a");
        var b = measured.Sessions.Single(session => session.PaneId == "pane-b");
        Assert.Equal((100L, 1, 0), (a.MemoryBytes, a.ProcessCount, a.SpawnedProcessCount));
        Assert.Equal((1400L, 2, 1), (b.MemoryBytes, b.ProcessCount, b.SpawnedProcessCount));
    }

    [Fact]
    public void Sample_CountsWhatASessionSpawned_WithoutItsOwnRootProcess()
    {
        // AC-1310: the axis the TTY status turns on. A live pane always holds its own pty, so a count that
        // included the root would say every open session is running something and the safety timeout that
        // rescues a stalled CLI would never fire again. Depth is not the question either — a grandchild counts.
        var table = new FixedProcessTable([new ProcessRow(11, 1, TimeSpan.Zero, 100)]);
        var monitor = new ResourceMonitor(table, _ => null);
        SessionProcessRef[] sessions = [new("pane-a", "A", 11)];

        var quiet = monitor.Sample(sessions).Sessions.Single();

        table.Rows = [.. table.Rows, new ProcessRow(12, 11, TimeSpan.Zero, 50), new ProcessRow(13, 12, TimeSpan.Zero, 50)];
        var working = monitor.Sample(sessions).Sessions.Single();

        Assert.Equal((1, 0), (quiet.ProcessCount, quiet.SpawnedProcessCount));
        Assert.Equal((3, 2), (working.ProcessCount, working.SpawnedProcessCount));
    }
}
