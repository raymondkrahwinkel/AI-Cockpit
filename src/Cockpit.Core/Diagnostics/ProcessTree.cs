namespace Cockpit.Core.Diagnostics;

/// <summary>
/// Adds a process up with everything it spawned (#78). This is the whole reason the meter is worth having: a
/// session <em>is</em> a <c>claude</c> process, but the CPU an operator wants to see is the build, the test run or
/// the grep it started. Measuring the parent alone would read 0% at precisely the moment they look.
/// </summary>
public static class ProcessTree
{
    public static ResourceSample Sum(IReadOnlyList<ProcessRow> rows, int rootProcessId)
    {
        var children = new Dictionary<int, List<int>>();
        var byId = new Dictionary<int, ProcessRow>();

        foreach (var row in rows)
        {
            byId[row.ProcessId] = row;

            if (!children.TryGetValue(row.ParentProcessId, out var list))
            {
                list = [];
                children[row.ParentProcessId] = list;
            }

            list.Add(row.ProcessId);
        }

        if (!byId.ContainsKey(rootProcessId))
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

            if (byId.TryGetValue(current, out var row))
            {
                cpu += row.CpuTime;
                memory += row.WorkingSetBytes;
            }

            if (children.TryGetValue(current, out var kids))
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
