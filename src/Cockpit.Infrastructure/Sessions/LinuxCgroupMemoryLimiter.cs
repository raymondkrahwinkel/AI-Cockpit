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

    private readonly ILogger _logger;
    private readonly Func<string?> _findWritableParent;

    public LinuxCgroupMemoryLimiter(ILogger<LinuxCgroupMemoryLimiter> logger)
        : this(logger, _FindWritableParent)
    {
    }

    // Test seam: real `/proc/self/cgroup` discovery needs actual cgroupfs, which no dev machine or CI runner for
    // this repo's other two platforms has. This lets a test point `Apply` at an ordinary temp directory instead
    // and read back what was really written there — real file I/O, just not real cgroupfs.
    internal LinuxCgroupMemoryLimiter(ILogger logger, Func<string?> findWritableParent)
    {
        _logger = logger;
        _findWritableParent = findWritableParent;
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

            var group = Path.Combine(parent, $"cockpit-session-{processId}");
            Directory.CreateDirectory(group);

            // memory.high, never memory.max (AC-692) — see the class comment. A session over this throttles; it is
            // never the reason the kernel's OOM killer looks at this cgroup.
            File.WriteAllText(Path.Combine(group, "memory.high"), capBytes.ToString());

            // Swap left as the parent has it: forcing memory.swap.max to 0 surprises a machine running zram.
            File.WriteAllText(Path.Combine(group, "cgroup.procs"), processId.ToString());

            _logger.LogInformation("Session {ProcessId} throttled past {CapBytes} bytes by cgroup {Group}.", processId, capBytes, group);
            return new CgroupHandle(group, _logger);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Session memory cap: could not create a cgroup; session {ProcessId} runs uncapped.", processId);
            return null;
        }
    }

    // The first level up where a new group both can be made and comes up with the memory controller's interface
    // files in it — proof the controller is delegated there, rather than a directory that merely accepted a mkdir.
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

    // A group only deletes once empty; one that still holds a process is left for systemd to reap with the session.
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
