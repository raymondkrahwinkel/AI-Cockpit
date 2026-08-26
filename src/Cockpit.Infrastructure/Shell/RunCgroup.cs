using Microsoft.Extensions.Logging;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Shell;

// AC-1094: a cgroup v2 group for one tracked run's process tree, reusing AC-1093's cgroup.kill — membership
// survives reparenting to pid 1, which `Kill(entireProcessTree: true)`'s ppid walk cannot reach. Naming reuses
// `LinuxCgroupMemoryLimiter`'s `cockpit-session-<owner>-...` scheme so a leaked group is caught by its startup sweep.
internal sealed class RunCgroup : IDisposable
{
    private readonly string? _group;
    private readonly ILogger _logger;

    private RunCgroup(string? group, ILogger logger)
    {
        _group = group;
        _logger = logger;
    }

    // Made before the run's process exists — see `ProcsPath`. A detached grandchild can reparent to pid 1 faster
    // than code here could react to a pid handed back after the fact, so the only race-free containment is the
    // child moving itself in before it runs anything at all.
    public static RunCgroup Create(string runId, ILogger logger) =>
        Create(runId, logger, LinuxCgroupMemoryLimiter.FindWritableParent);

    // Test seam: same reason as `LinuxCgroupMemoryLimiter` — no dev machine or CI runner for this repo's other two
    // platforms has real cgroupfs to point this at.
    internal static RunCgroup Create(string runId, ILogger logger, Func<string?> findWritableParent)
    {
        try
        {
            if (findWritableParent() is not { } parent)
            {
                return new RunCgroup(null, logger);
            }

            var group = Path.Combine(parent, $"{LinuxCgroupMemoryLimiter.GroupPrefix}{Environment.ProcessId}-{runId}");
            Directory.CreateDirectory(group);

            return new RunCgroup(group, logger);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Run {RunId}: could not create a cgroup; its process tree is not contained.", runId);
            return new RunCgroup(null, logger);
        }
    }

    // Whether this run is actually contained — false means no writable cgroup v2 parent was found (non-Linux, no
    // delegation), and the caller has nothing to point a not-yet-started process's self-move at.
    public bool IsContained => _group is not null;

    // The `cgroup.procs` file the run's own about-to-start process writes its pid to as the very first thing it
    // does — before it forks, before it execs the real command — so it is a member of this group from birth. Null
    // when this run is not contained, which is the caller's cue to skip the wrapper and run the command directly.
    public string? ProcsPath => _group is { } group ? Path.Combine(group, "cgroup.procs") : null;

    // Ends every process the run's tree still holds — reparented nodes included — and removes the group. Safe to
    // call more than once, and unconditionally: a normal exit can still leave a reused build server running, which
    // is exactly the case a ppid-tree kill would never have caught either.
    public void KillAll()
    {
        if (_group is not { } group)
        {
            return;
        }

        if (LinuxCgroupMemoryLimiter.KillGroup(group) is { } reason)
        {
            _logger.LogWarning("Run cgroup {Group}: could not end what it still holds — {Reason}.", group, reason);
        }
    }

    public void Dispose()
    {
        if (_group is not { } group)
        {
            return;
        }

        try
        {
            Directory.Delete(group);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The kill lands at once but the group is only empty once the last corpse is reaped — the next start's
            // sweep removes what is left, same as `LinuxCgroupMemoryLimiter.CgroupHandle`.
            _logger.LogDebug(exception, "Run cgroup {Group}: not removed; it still holds processes.", group);
        }
    }
}
