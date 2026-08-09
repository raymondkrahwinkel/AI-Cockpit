using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// Linux `ISessionMemoryLimiter` (AC-661): a cgroup v2 group per session with `memory.max` set, the session's pid
// moved into it. Everything the session spawns is born in the same cgroup, so the ceiling covers the tree, and the
// kernel's OOM kill is scoped to that cgroup — the cockpit sits in its own and is never a candidate.
//
// Plain cgroupfs rather than `systemd-run --scope`: the pid is already running by the time we cap it (the same call
// has to serve the pty route and a plugin driver that spawned its own child), and systemd-run only wraps a launch.
// The group is created next to the cockpit's own, inside the subtree systemd delegates to the user session, which is
// where a user process is allowed to make one.
internal sealed class LinuxCgroupMemoryLimiter(ILogger<LinuxCgroupMemoryLimiter> logger) : ISessionMemoryLimiter
{
    private const string CgroupRoot = "/sys/fs/cgroup";

    // How far up from our own cgroup to look for a level we may create a group under. Our own has processes in it,
    // so it cannot enable the memory controller for children; the delegation boundary is a level or two above.
    private const int SearchDepth = 4;

    public string Mechanism => "cgroup v2 memory.max";

    public IDisposable? Apply(int processId, long capBytes)
    {
        try
        {
            if (_FindWritableParent() is not { } parent)
            {
                logger.LogWarning("Session memory cap: no writable cgroup v2 parent with a memory controller; session {ProcessId} runs uncapped.", processId);
                return null;
            }

            var group = Path.Combine(parent, $"cockpit-session-{processId}");
            Directory.CreateDirectory(group);
            File.WriteAllText(Path.Combine(group, "memory.max"), capBytes.ToString());

            // Swap is left as the parent has it: capping memory while swap stays open only moves the blow-up to disk,
            // but forcing memory.swap.max to 0 on a machine that relies on zram is its own kind of surprise.
            File.WriteAllText(Path.Combine(group, "cgroup.procs"), processId.ToString());

            logger.LogInformation("Session {ProcessId} capped at {CapBytes} bytes by cgroup {Group}.", processId, capBytes, group);
            return new CgroupHandle(group, logger);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Session memory cap: could not create a cgroup; session {ProcessId} runs uncapped.", processId);
            return null;
        }
    }

    // Walks up from the cockpit's own cgroup for the first level where a new group both can be made and comes up
    // with `memory.max` in it — proof the memory controller is actually delegated there, rather than a directory
    // that merely accepted a mkdir.
    private static string? _FindWritableParent()
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
                var usable = File.Exists(Path.Combine(probe, "memory.max"));
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

    // Removing the group is only possible once it is empty, which is exactly when the session's tree has gone. A
    // group that still holds a process is left behind rather than forced — systemd reaps it with the user session.
    private sealed class CgroupHandle(string group, ILogger logger) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Directory.Delete(group);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(exception, "Session memory cap: cgroup {Group} not removed; it still holds processes.", group);
            }
        }
    }
}
