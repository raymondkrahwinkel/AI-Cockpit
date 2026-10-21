using System.Globalization;

namespace Cockpit.Core.HeavyRuns;

// AC-1535: who may start next, decided on the measured load rather than a fixed count. A run gets in when the load
// plus what its kind is known to add stays under the ceilings; with no heavy run of the cockpit's going, the head of
// the line always gets in, whatever else loads the machine, so nothing waits forever.
public static class HeavyRunAdmission
{
    // Pre-merge before explore, first come first served within a rank; an explore that waited long enough ranks up.
    public static IReadOnlyList<int> Order(IReadOnlyList<HeavyRunCandidate> waiting, HeavyRunSettings settings, DateTimeOffset now) =>
        [.. Enumerable.Range(0, waiting.Count)
            .OrderByDescending(index => _Rank(waiting[index], settings, now))
            .ThenBy(index => waiting[index].EnqueuedAt)];

    // Only the head of the line is considered: a smaller run behind it never overtakes, or a gate could starve.
    public static HeavyRunDecision Next(
        IReadOnlyList<HeavyRunCandidate> waiting, MachineLoad load, HeavyRunSettings settings, bool isAnyHeavyRunning, DateTimeOffset now)
    {
        if (waiting.Count == 0)
        {
            return new HeavyRunDecision(null, null);
        }

        var head = Order(waiting, settings, now)[0];
        var reason = WaitReason(waiting[head], load, settings, isAnyHeavyRunning);
        return reason is null ? new HeavyRunDecision(head, null) : new HeavyRunDecision(null, reason);
    }

    public static string? WaitReason(HeavyRunCandidate candidate, MachineLoad load, HeavyRunSettings settings, bool isAnyHeavyRunning)
    {
        if (!isAnyHeavyRunning)
        {
            return null;
        }

        var cost = candidate.Cost;
        var kind = candidate.Kind;
        if (load.CpuPercent + (cost?.CpuPercent ?? 0) > settings.CpuCeilingPercent)
        {
            return $"waiting: CPU {load.CpuPercent.ToString("0", CultureInfo.InvariantCulture)}%";
        }

        var memoryAfter = load.UsedMemoryBytes + (cost?.MemoryBytes ?? 0);
        if (load.TotalMemoryBytes > 0 && 100.0 * memoryAfter / load.TotalMemoryBytes > settings.MemoryCeilingPercent)
        {
            return cost is null
                ? $"waiting: memory {load.MemoryPercent.ToString("0", CultureInfo.InvariantCulture)}%"
                : $"waiting: {_Gb(load.AvailableMemoryBytes)} GB memory free, {kind} needs ±{_Gb(cost.MemoryBytes)} GB";
        }

        if (cost is null)
        {
            return $"waiting: {kind} has no measured cost yet, so it runs alone";
        }

        return candidate.FreeDiskBytes is { } free && cost.DiskBytes > free
            ? $"waiting: {_Gb(free)} GB disk free, {kind} needs ±{_Gb(cost.DiskBytes)} GB"
            : null;
    }

    // The signatures of a run that failed because the machine was too busy rather than because the code is wrong.
    public static bool LooksOverloaded(string output) =>
        output.Contains("Command Timeout expired", StringComparison.OrdinalIgnoreCase)
        || output.Contains("unhealthy", StringComparison.OrdinalIgnoreCase);

    private static int _Rank(HeavyRunCandidate candidate, HeavyRunSettings settings, DateTimeOffset now) =>
        candidate.IsPreMerge || now - candidate.EnqueuedAt >= TimeSpan.FromMinutes(settings.AgingMinutes) ? 1 : 0;

    private static string _Gb(long bytes) => (bytes / 1_073_741_824.0).ToString("0.0", CultureInfo.InvariantCulture);
}
