using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.ViewTests;

// AC-1086: the cockpit-wide figure a shared memory budget leans on. It has to see what the per-session meter sees,
// and a walk down parent links cannot — an open session's process outlives the launcher the walk needs to find it.
public sealed class ResourceMonitorAbandonedProcessTests
{
    private const int SessionProcessId = 900_001;
    private const int SpawnedProcessId = 900_002;

    // A parent id that is in no process table here, which is what a session's launcher looks like once it is gone.
    private const int DeadParentProcessId = 900_003;

    private const int SecondSessionProcessId = 900_004;

    [Fact]
    public void Sample_AfterASessionsChildIsOrphaned_StillCountsItInTheCockpitWideTotal()
    {
        var table = new MutableProcessTable(_Rows(spawnedParentProcessId: SessionProcessId));
        var monitor = new ResourceMonitor(table, _ => null);
        var sessions = new[] { new SessionProcessRef("pane-a", "Session", SessionProcessId) };

        // First sample, while the parent link still holds: this is where the process joins the session's membership.
        monitor.Sample(sessions);

        table.Rows = _Rows(spawnedParentProcessId: DeadParentProcessId);
        var usage = monitor.Sample(sessions);

        // 100 (cockpit) + 200 (session) + 400 (the orphan). Without the union the walk stops at 300, and the budget
        // would be blind to the one process that is worth reporting.
        Assert.Equal(700, usage.MemoryBytes);
        Assert.Equal(1, usage.Sessions.Single().AbandonedProcessCount);
    }

    [Fact]
    public void Sample_DoesNotCountASessionsProcessesTwice()
    {
        // Everything still hangs off the cockpit, so the tree and the membership overlap completely — summing the
        // two would read 900 for a machine holding 700.
        var table = new MutableProcessTable(_Rows(spawnedParentProcessId: SessionProcessId));
        var monitor = new ResourceMonitor(table, _ => null);
        var sessions = new[] { new SessionProcessRef("pane-a", "Session", SessionProcessId) };

        monitor.Sample(sessions);

        Assert.Equal(700, monitor.Sample(sessions).MemoryBytes);
    }

    [Fact]
    public void Sample_AfterTheSessionIsGone_DropsItsProcessesFromTheTotal()
    {
        // A closed session must not keep inflating the budget with pids it no longer owns; its processes are still
        // in the table and still orphaned, so only forgetting the membership takes them out. This is equally the
        // budget's known limit — what a closed session left behind is counted by nobody, and AC-1260 owns that.
        var table = new MutableProcessTable(_Rows(spawnedParentProcessId: SessionProcessId));
        var monitor = new ResourceMonitor(table, _ => null);

        monitor.Sample([new SessionProcessRef("pane-a", "Session", SessionProcessId)]);

        table.Rows =
        [
            new ProcessRow(Environment.ProcessId, 0, TimeSpan.Zero, 100),
            new ProcessRow(SessionProcessId, DeadParentProcessId, TimeSpan.Zero, 200),
            new ProcessRow(SpawnedProcessId, SessionProcessId, TimeSpan.Zero, 400),
        ];

        Assert.Equal(100, monitor.Sample([]).MemoryBytes);
    }

    [Fact]
    public void Sample_WhenOneSessionClosesAsAnotherOpens_TheClosedOneStillDropsOut()
    {
        // The count that decides whether to prune stays level across that swap, so it is worth pinning that it
        // still prunes: `_previous` only ever grows between prunes, so it exceeds the session count the moment any
        // measured process is no longer measured — a swap included.
        var table = new MutableProcessTable(_Rows(spawnedParentProcessId: SessionProcessId));
        var monitor = new ResourceMonitor(table, _ => null);

        monitor.Sample([new SessionProcessRef("pane-a", "Session", SessionProcessId)]);

        table.Rows =
        [
            new ProcessRow(Environment.ProcessId, 0, TimeSpan.Zero, 100),
            new ProcessRow(SessionProcessId, DeadParentProcessId, TimeSpan.Zero, 200),
            new ProcessRow(SpawnedProcessId, SessionProcessId, TimeSpan.Zero, 400),
            new ProcessRow(SecondSessionProcessId, Environment.ProcessId, TimeSpan.Zero, 50),
        ];

        var usage = monitor.Sample([new SessionProcessRef("pane-b", "Session", SecondSessionProcessId)]);

        // 100 (cockpit) + 50 (the session that just opened). The 600 the closed one left behind is nobody's.
        Assert.Equal(150, usage.MemoryBytes);
    }

    private static ProcessRow[] _Rows(int spawnedParentProcessId) =>
    [
        new(Environment.ProcessId, 0, TimeSpan.Zero, 100),
        new(SessionProcessId, Environment.ProcessId, TimeSpan.Zero, 200),
        new(SpawnedProcessId, spawnedParentProcessId, TimeSpan.Zero, 400),
    ];

    private sealed class MutableProcessTable(IReadOnlyList<ProcessRow> rows) : IProcessTableReader
    {
        public IReadOnlyList<ProcessRow> Rows { get; set; } = rows;

        public IReadOnlyList<ProcessRow> Read() => Rows;
    }
}
