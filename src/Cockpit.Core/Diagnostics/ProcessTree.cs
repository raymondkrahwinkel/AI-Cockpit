namespace Cockpit.Core.Diagnostics;

// Adds a process up with everything it spawned (#78). This is the whole reason the meter is worth having: a
// session *is* a `claude` process, but the CPU an operator wants to see is the build, the test run or
// the grep it started. Measuring the parent alone would read 0% at precisely the moment they look.
public static class ProcessTree
{
    public static ResourceSample Sum(IReadOnlyList<ProcessRow> rows, int rootProcessId) =>
        Snapshot(rows).Sum(rootProcessId);

    // The one snapshot a cached read already built, or a fresh one — so a second caller on the same tick pays for
    // indexing the table once rather than twice (AC-1233).
    public static ProcessTableSnapshot Snapshot(IReadOnlyList<ProcessRow> rows) =>
        rows is ProcessTableSnapshotRows snapshotRows
            ? snapshotRows.Snapshot
            : new ProcessTableSnapshot(rows);
}

public sealed class ProcessTableSnapshot
{
    private readonly Dictionary<int, List<int>> _children = [];
    private readonly Dictionary<int, ProcessRow> _byId = [];

    public ProcessTableSnapshot(IReadOnlyList<ProcessRow> rows)
    {
        foreach (var row in rows)
        {
            _byId[row.ProcessId] = row;

            if (!_children.TryGetValue(row.ParentProcessId, out var list))
            {
                list = [];
                _children[row.ParentProcessId] = list;
            }

            list.Add(row.ProcessId);
        }
    }

    // An empty result is an exited session, not an error.
    public ResourceSample Sum(int rootProcessId) => SumOf(LiveReachableFrom([rootProcessId]));

    // AC-1096: every live process reachable from `seeds` by parent links, the seeds themselves included. Seeds
    // that are no longer in the table drop out, which is how a remembered membership shrinks as processes exit.
    public HashSet<int> LiveReachableFrom(IEnumerable<int> seeds)
    {
        var reached = new HashSet<int>();
        var pending = new Stack<int>(seeds);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            // A process table read while processes come and go can contain a cycle (a reused id whose parent
            // now points back into the tree). Visiting each id once makes the walk terminate regardless.
            if (!_byId.ContainsKey(current) || !reached.Add(current))
            {
                continue;
            }

            if (_children.TryGetValue(current, out var kids))
            {
                foreach (var kid in kids)
                {
                    pending.Push(kid);
                }
            }
        }

        return reached;
    }

    // AC-1096: members whose parent is no longer one of them — on Windows a dead pid, on Linux the init process
    // that adopted them. These are exactly the ones a walk from `rootProcessId` can no longer reach.
    public int AbandonedCount(IReadOnlySet<int> members, int rootProcessId)
    {
        var abandoned = 0;

        foreach (var processId in members)
        {
            if (processId != rootProcessId
                && _byId.TryGetValue(processId, out var row)
                && !members.Contains(row.ParentProcessId))
            {
                abandoned++;
            }
        }

        return abandoned;
    }

    // AC-1096: weighs an explicit set rather than a tree, for a membership the parent chain can no longer describe.
    public ResourceSample SumOf(IReadOnlyCollection<int> processIds)
    {
        var cpu = TimeSpan.Zero;
        var memory = 0L;

        foreach (var processId in processIds)
        {
            if (_byId.TryGetValue(processId, out var row))
            {
                cpu += row.CpuTime;
                memory += row.WorkingSetBytes;
            }
        }

        return new ResourceSample(cpu, memory);
    }
}
