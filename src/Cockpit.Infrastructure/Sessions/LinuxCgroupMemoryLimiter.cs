using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// Linux `ISessionMemoryLimiter` (AC-661): a cgroup v2 group per session, the pid moved in. AC-692: sets
// `memory.high` (a throttle) rather than `memory.max` (a hard OOM-kill boundary), since Cockpit no longer ends a
// session for going over its cap on any platform — it warns instead, by toast and by the session's bar (AC-700).
internal sealed class LinuxCgroupMemoryLimiter : ISessionMemoryLimiter
{
    private const string CgroupRoot = "/sys/fs/cgroup";

    // Our own cgroup has processes in it, so it cannot enable the memory controller for children; the delegation
    // boundary is a level or two above.
    private const int SearchDepth = 4;

    // AC-1093: what marks a directory in the parent as a session group of ours, so a later run can find back what
    // this one left behind. A cgroup outlives the process that made it, which is the whole point of the anchor.
    internal const string GroupPrefix = "cockpit-session-";

    private readonly ILogger _logger;
    private readonly Func<string?> _findWritableParent;
    private readonly Func<int, IReadOnlyList<int>> _readChildren;

    public LinuxCgroupMemoryLimiter(ILogger<LinuxCgroupMemoryLimiter> logger)
        : this(logger, FindWritableParent, _ReadChildren)
    {
    }

    // Test seam: real `/proc/self/cgroup` discovery and `/proc/<pid>/task/*/children` need actual cgroupfs and a
    // live process tree, which no dev machine or CI runner for this repo's other two platforms has. This lets a
    // test point `Apply` at an ordinary temp directory instead and read back what was really written there.
    internal LinuxCgroupMemoryLimiter(ILogger logger, Func<string?> findWritableParent, Func<int, IReadOnlyList<int>>? readChildren = null)
    {
        _logger = logger;
        _findWritableParent = findWritableParent;
        _readChildren = readChildren ?? (_ => []);
    }

    public IDisposable? Apply(int processId, long capBytes)
    {
        try
        {
            if (_findWritableParent() is not { } parent)
            {
                _logger.LogWarning("Session memory cap: no writable cgroup v2 parent with a memory controller; session {ProcessId} runs uncapped.", processId);
                return null;
            }

            var group = Path.Combine(parent, GroupNameFor(Environment.ProcessId, processId));
            Directory.CreateDirectory(group);

            // memory.high, never memory.max (AC-692) — see the class comment. A session over this throttles; it is
            // never the reason the kernel's OOM killer looks at this cgroup.
            File.WriteAllText(Path.Combine(group, "memory.high"), capBytes.ToString());

            // Swap left as the parent has it: forcing memory.swap.max to 0 surprises a machine running zram.
            // The session itself goes first, so everything it forks from here on is born inside the group — and
            // not through `_Enrol`, since failing to move the session means there is no cap and the caller must hear it.
            var procs = Path.Combine(group, "cgroup.procs");
            File.AppendAllText(procs, processId + "\n");

            var adopted = _AdoptExistingDescendants(procs, processId);

            _logger.LogInformation("Session {ProcessId} throttled past {CapBytes} bytes by cgroup {Group}.", processId, capBytes, group);
            if (adopted > 0)
            {
                _logger.LogInformation("Session {ProcessId}: {Adopted} process(es) it had already forked were moved in with it.", processId, adopted);
            }

            return new CgroupHandle(group, _logger);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Session memory cap: could not create a cgroup; session {ProcessId} runs uncapped.", processId);
            return null;
        }
    }

    // AC-1086: a cgroup is inherited at fork, so a child born before the move stays where it started. The agent CLI
    // starts its MCP servers within a second of spawning while this call lands three to four seconds later, which
    // left ~137 MB per session outside the cap and charged to the cockpit's own scope. Returns how many moved.
    private int _AdoptExistingDescendants(string procs, int rootProcessId)
    {
        var adopted = 0;
        var pending = new Stack<int>();
        pending.Push(rootProcessId);

        // A process table that changes while it is walked can present a cycle; visiting each id once terminates
        // regardless, the same guard `ProcessTree.Sum` needs for the same reason.
        var seen = new HashSet<int> { rootProcessId };

        while (pending.Count > 0)
        {
            foreach (var child in _Children(pending.Pop()))
            {
                if (!seen.Add(child))
                {
                    continue;
                }

                pending.Push(child);

                // Per child, not per sweep: one that exited between being listed and being written is ordinary,
                // and must not cost the siblings behind it their move.
                if (_Enrol(procs, child))
                {
                    adopted++;
                }
            }
        }

        return adopted;
    }

    // By the time the sweep runs the group exists and the session is in it, so a stumble here must cost the strays
    // it has not reached yet and nothing more — never the handle, which is what removes the group again.
    private IReadOnlyList<int> _Children(int processId)
    {
        try
        {
            return _readChildren(processId);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Session memory cap: could not read the children of {ProcessId}; it keeps the ones it has.", processId);
            return [];
        }
    }

    // Appends rather than overwrites: cgroupfs takes one pid per write and ignores the offset, and appending is
    // what makes a plain temp directory show every pid a test wrote instead of only the last.
    private static bool _Enrol(string procs, int processId)
    {
        try
        {
            File.AppendAllText(procs, processId + "\n");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The children of every thread of `processId`, from procfs. A process with no `/proc` entry has exited, which
    // is an ordinary outcome here rather than an error.
    private static IReadOnlyList<int> _ReadChildren(int processId)
    {
        var tasks = $"/proc/{processId}/task";
        if (!Directory.Exists(tasks))
        {
            return [];
        }

        var children = new List<int>();
        try
        {
            foreach (var task in Directory.EnumerateDirectories(tasks))
            {
                foreach (var id in File.ReadAllText(Path.Combine(task, "children")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(id, out var child))
                    {
                        children.Add(child);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A thread that ended mid-walk takes its `children` file with it; what was read already still counts.
        }

        return children;
    }

    // Carries the cockpit that made the group as well as the session in it. The owner is what tells a leftover
    // group of a run that died from a live group of the cockpit beside this one — a development build takes no
    // single-instance claim (AC-4), so two cockpits sharing one parent is ordinary rather than impossible.
    internal static string GroupNameFor(int ownerProcessId, int sessionProcessId) =>
        $"{GroupPrefix}{ownerProcessId}-{sessionProcessId}";

    // The cockpit a group directory names as its maker, or null when the name does not carry one — which is what
    // a group from before AC-1093 looks like, and those can only be a previous run's.
    internal static int? OwnerOf(string groupName)
    {
        if (!groupName.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = groupName[GroupPrefix.Length..].Split('-');

        return parts.Length is 2 && int.TryParse(parts[0], out var owner) ? owner : null;
    }

    // AC-1093: cgroup v2's own `cgroup.kill` (kernel 5.14+) — one write ends every process in the group, with no
    // process tree to walk. That is what reaches an MSBuild node or a VBCSCompiler that outlived its build and was
    // adopted by systemd: it left the tree, never the cgroup. Returns why it could not, or null when it did.
    internal static string? KillGroup(string group)
    {
        var kill = Path.Combine(group, "cgroup.kill");
        if (!File.Exists(kill))
        {
            return "this kernel's cgroup v2 has no cgroup.kill (5.14 or newer)";
        }

        try
        {
            File.WriteAllText(kill, "1\n");

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    // The first level up where a new group both can be made and comes up with the memory controller's interface
    // files in it — proof the controller is delegated there, rather than a directory that merely accepted a mkdir.
    internal static string? FindWritableParent()
    {
        if (_OwnCgroupPath() is not { } own)
        {
            return null;
        }

        var candidate = Path.GetDirectoryName(own);
        for (var level = 0; level < SearchDepth && !string.IsNullOrEmpty(candidate) && candidate.StartsWith(CgroupRoot, StringComparison.Ordinal); level++)
        {
            var probe = Path.Combine(candidate, ".cockpit-cap-probe");
            try
            {
                Directory.CreateDirectory(probe);
                var usable = File.Exists(Path.Combine(probe, "memory.high"));
                Directory.Delete(probe);
                if (usable)
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Not ours to write in; try the level above.
            }

            candidate = Path.GetDirectoryName(candidate);
        }

        return null;
    }

    // `/proc/self/cgroup` on v2 is the single line `0::<path>`, relative to the cgroup mount.
    private static string? _OwnCgroupPath()
    {
        const string self = "/proc/self/cgroup";
        if (!File.Exists(self))
        {
            return null;
        }

        foreach (var line in File.ReadLines(self))
        {
            if (line.StartsWith("0::", StringComparison.Ordinal))
            {
                return CgroupRoot + line[3..].TrimEnd();
            }
        }

        return null;
    }

    // A group only deletes once empty, so what the session left running goes first. The pty child has already had
    // its SIGHUP by now (`TtyProcessOwningSessionFiles.Dispose` releases this handle last), so this is the tail:
    // the build servers that ignore it and the strays systemd has adopted.
    private sealed class CgroupHandle(string group, ILogger logger) : IDisposable
    {
        public void Dispose()
        {
            // AC-1093: no silence when it could not be done — a session whose leftovers are still running is
            // exactly the thing that went unnoticed for 35 minutes on 26-08-2026.
            if (KillGroup(group) is { } reason)
            {
                logger.LogWarning("Session cgroup {Group}: could not stop what it still holds — {Reason}.", group, reason);
            }

            try
            {
                Directory.Delete(group);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The kill lands at once but the group is only empty once the last corpse is reaped, so an
                // immediate rmdir can still lose that race. The next start's sweep removes what is left.
                logger.LogDebug(exception, "Session memory cap: cgroup {Group} not removed; it still holds processes.", group);
            }
        }
    }
}
