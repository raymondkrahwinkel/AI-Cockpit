using Cockpit.App.Services;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.ViewTests;

public sealed class ResourceMonitorAllocationTests
{
    [Fact]
    public void Sample_With400RowsAndEightSessions_StaysWithinTheAllocationBudget()
    {
        var rows = Enumerable.Range(1, 400)
            .Select(id => new ProcessRow(id, id <= 8 ? 0 : (id % 8) + 1, TimeSpan.FromSeconds(id), 1024))
            .ToArray();
        var monitor = new ResourceMonitor(new CachedProcessTableReader(new FixedProcessTable(rows)), _ => null);
        var sessions = _Sessions(8);

        monitor.Sample(sessions);
        var before = GC.GetAllocatedBytesForCurrentThread();
        monitor.Sample(sessions);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 80_000);
    }

    [Fact]
    public void Sample_AllocationDoesNotScaleWithProcessTableIndexingPerSession()
    {
        var rows = Enumerable.Range(1, 400).Select(id => new ProcessRow(id, id <= 8 ? 0 : (id % 8) + 1, TimeSpan.Zero, 1024)).ToArray();
        var one = _Allocated(new ResourceMonitor(new CachedProcessTableReader(new FixedProcessTable(rows)), _ => null), _Sessions(1));
        var eight = _Allocated(new ResourceMonitor(new CachedProcessTableReader(new FixedProcessTable(rows)), _ => null), _Sessions(8));

        Assert.InRange(eight - one, 0, 48_000);
    }

    private static SessionProcessRef[] _Sessions(int count) =>
        [.. Enumerable.Range(1, count).Select(id => new SessionProcessRef($"pane-{id}", $"session-{id}", id))];

    private static long _Allocated(ResourceMonitor monitor, IReadOnlyList<SessionProcessRef> sessions)
    {
        monitor.Sample(sessions);
        var before = GC.GetAllocatedBytesForCurrentThread();
        monitor.Sample(sessions);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
