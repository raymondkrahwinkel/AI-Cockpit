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

    // AC-1331: live processes with a shell on the path from the root — work the session started and waits on. The
    // root is never counted and marks the path only for a terminal pane (`rootIsShell`), since on Windows the `.cmd`
    // shim makes `cmd.exe` every agent's root. ponytail: an MCP server launched via `sh -c` counts as work for life.
    public int OutstandingCount(IReadOnlySet<int> members, int rootProcessId, bool rootIsShell)
    {
        var budget = members.Count;
        return _OutstandingBelow(rootProcessId, rootIsShell, ref budget);
    }

    // Recursive so a sample allocates nothing (AC-1233). `budget` bounds the walk: a table read while processes
    // come and go can contain a cycle, and a live tree never has more processes than the membership holds.
    private int _OutstandingBelow(int processId, bool underShell, ref int budget)
    {
        if (!_children.TryGetValue(processId, out var kids))
        {
            return 0;
        }

        var outstanding = 0;
        foreach (var kid in kids)
        {
            if (--budget < 0)
            {
                break;
            }

            var shellOnPath = underShell || _IsShell(_byId[kid].Name);
            outstanding += (shellOnPath ? 1 : 0) + _OutstandingBelow(kid, shellOnPath, ref budget);
        }

        return outstanding;
    }

    // `ps` on macOS reports a login shell as `-zsh` and may carry the path; Windows reports `cmd.exe`.
    private static bool _IsShell(string name) =>
        ShellNames.Contains(Path.GetFileName(name).TrimStart('-'));

    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "zsh", "dash", "fish", "ksh", "cmd.exe", "powershell.exe", "pwsh.exe",
    };

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
