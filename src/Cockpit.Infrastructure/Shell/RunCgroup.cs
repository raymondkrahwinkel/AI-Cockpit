using Microsoft.Extensions.Logging;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Shell;

// AC-1094: a cgroup v2 group for exactly one tracked run's process tree, reusing the primitive AC-1093 built for
// session containment. `Process.Kill(entireProcessTree: true)` walks ppid, so a build-server node that outlived its
// parent and was adopted by pid 1 falls outside the walk; cgroup membership is inherited at fork and untouched by
// reparenting, so `cgroup.kill` reaches it regardless. Naming reuses `LinuxCgroupMemoryLimiter`'s own
// `cockpit-session-<owner>-...` scheme rather than a new prefix, so a group this leaves behind (a crash mid-run) is
// picked up by the same startup sweep that already cleans up leftover session groups — `OwnerOf` only ever parses
// the owner half, so `runId` (never containing '-') is free to be a token instead of a pid.
internal sealed class RunCgroup : IDisposable
{
    private readonly string? _group;
    private readonly ILogger _logger;

    private RunCgroup(string? group, ILogger logger)
    {
        _group = group;
        _logger = logger;
    }

    // Made before the run's process exists — see `ProcsPath`. There is nothing to move afterward: a process that
    // forks a detached grandchild can reparent it to pid 1 faster than any code on our side could react to the
    // process id `Process.Start()` hands back, so the only race-free containment is the child moving itself in
    // before it runs anything at all.
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
