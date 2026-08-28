namespace Cockpit.Core.Diagnostics;

// Adds a process up with everything it spawned (#78). This is the whole reason the meter is worth having: a
// session *is* a `claude` process, but the CPU an operator wants to see is the build, the test run or
// the grep it started. Measuring the parent alone would read 0% at precisely the moment they look.
public static class ProcessTree
{
    public static ResourceSample Sum(IReadOnlyList<ProcessRow> rows, int rootProcessId) =>
        rows is ProcessTableSnapshotRows snapshotRows
            ? snapshotRows.Snapshot.Sum(rootProcessId)
            : new ProcessTableSnapshot(rows).Sum(rootProcessId);
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

    public ResourceSample Sum(int rootProcessId)
    {
        if (!_byId.ContainsKey(rootProcessId))
        {
            // The session's process is gone — that is an exited session, not an error.
            return ResourceSample.None;
        }

        var cpu = TimeSpan.Zero;
        var memory = 0L;

        var pending = new Stack<int>();
        pending.Push(rootProcessId);
        var seen = new HashSet<int>();

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            // A process table read while processes come and go can contain a cycle (a reused id whose parent
            // now points back into the tree). Visiting each id once makes the walk terminate regardless.
            if (!seen.Add(current))
            {
                continue;
            }

            if (_byId.TryGetValue(current, out var row))
            {
                cpu += row.CpuTime;
                memory += row.WorkingSetBytes;
            }

            if (_children.TryGetValue(current, out var kids))
            {
                foreach (var kid in kids)
                {
                    pending.Push(kid);
                }
            }
        }

        return new ResourceSample(cpu, memory);
    }
}
