using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions;

// AC-1093: ends the processes that sessions of a previous run left behind, found by the cgroup that outlived them,
// and reports what it could not end. Runs from `Program.Main` on every start rather than from a container singleton,
// for the same reason as `CredentialFileHousekeeping`: a start that opens no session would never trigger it.
public static class StaleSessionProcessSweep
{
    // Sweeps what the previous run left, or says why this platform cannot.
    public static void Run(ILogger logger)
    {
        // AC-692: Windows and macOS run `PollingMemoryLimiter`, which measures and contains nothing. There is no
        // group to end and no anchor to find one by, and that is a reported outcome rather than a quiet skip.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            logger.LogWarning(
                "Leftover session processes: this platform has no per-session containment (AC-692), so anything a previous run's session left running keeps running.");

            return;
        }

        try
        {
            _Report(Sweep(LinuxCgroupMemoryLimiter.FindWritableParent, _IsRunning, Environment.ProcessId), logger);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Leftover session processes: the sweep could not be run; anything left over keeps running.");
        }
    }

    // What one sweep did. `Unavailable` is set only when there was no anchor to sweep by at all, which is a
    // different answer from having swept nothing.
    internal sealed record SweepOutcome(int Groups, int Processes, IReadOnlyList<string> Failures, string? Unavailable = null);

    internal static SweepOutcome Sweep(Func<string?> findWritableParent, Func<int, bool> isRunning, int ownProcessId)
    {
        if (findWritableParent() is not { } parent)
        {
            return new SweepOutcome(0, 0, [], "no writable cgroup v2 parent with a memory controller");
        }

        var groups = 0;
        var processes = 0;
        var failures = new List<string>();

        foreach (var group in Directory.EnumerateDirectories(parent, LinuxCgroupMemoryLimiter.GroupPrefix + "*"))
        {
            var name = Path.GetFileName(group);

            // A group whose cockpit is still running holds that cockpit's live session, and killing it would be
            // the very thing this must never do. Our own pid is the exception: nothing of ours exists yet at this
            // point in the start, so a group naming us is a dead run whose pid has come round again.
            if (LinuxCgroupMemoryLimiter.OwnerOf(name) is { } owner && owner != ownProcessId && isRunning(owner))
            {
                continue;
            }

            var held = _Held(group);
            if (LinuxCgroupMemoryLimiter.KillGroup(group) is { } reason)
            {
                failures.Add($"{name}: {reason}");
                continue;
            }

            groups++;
            processes += held;
            _Remove(group);
        }

        return new SweepOutcome(groups, processes, failures);
    }

    // How many processes the group still held, read before the kill so the log can say what was ended rather than
    // that something was. An unreadable group still gets killed; only the figure is lost.
    private static int _Held(string group)
    {
        try
        {
            return File.ReadAllLines(Path.Combine(group, "cgroup.procs")).Count(line => line.Length > 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // Tidiness, not the point: the kill is what the criterion asks for, and the group is only removable once the
    // last corpse has been reaped. One that loses that race is picked up by the next start's sweep.
    private static void _Remove(string group)
    {
        try
        {
            Directory.Delete(group);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // procfs is the whole check. A pid that has come round again onto an unrelated process makes this skip a group
    // it could have cleaned, which is the harmless direction to be wrong in.
    private static bool _IsRunning(int processId) => Directory.Exists($"/proc/{processId}");

    private static void _Report(SweepOutcome outcome, ILogger logger)
    {
        if (outcome.Unavailable is { } unavailable)
        {
            logger.LogWarning(
                "Leftover session processes: no anchor to clean them up by — {Reason}. Anything a previous run's session left running keeps running.",
                unavailable);

            return;
        }

        foreach (var failure in outcome.Failures)
        {
            logger.LogWarning("Leftover session processes: {Failure}. Those processes keep running.", failure);
        }

        if (outcome.Groups > 0)
        {
            logger.LogInformation(
                "Leftover session processes: stopped {Processes} process(es) from {Groups} session(s) of a previous run.",
                outcome.Processes,
                outcome.Groups);
        }
    }
}
