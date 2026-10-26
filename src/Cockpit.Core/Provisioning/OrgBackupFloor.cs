using Cockpit.Core.Backup;

namespace Cockpit.Core.Provisioning;

// AC-1593 (manifest-v1 §3.7): the schedule a run keeps to under the organisation's backup floor. Nothing is written back:
// `required` switches it on, the interval is at most and `keep` at least the floor, and a target outside the list gives
// way to the first allowed one registered here. With none of those, the run says so and blocks nothing else (B7).
public static class OrgBackupFloor
{
    public static (BackupSchedule Schedule, string? Refusal) Apply(ProvisionPolicy? policy, BackupSchedule local, IReadOnlyList<string> registered)
    {
        if (policy?.Backup is not { } floor)
        {
            return (local, null);
        }

        var schedule = local with
        {
            Enabled = local.Enabled || floor.Required is true,
            IntervalHours = floor.MaxIntervalHours is { } most ? Math.Min(local.IntervalHours, most) : local.IntervalHours,
            Keep = floor.MinKeep is { } least ? Math.Max(local.Keep, least) : local.Keep,
        };
        if (floor.AllowedTargets is not { } allowed || _Allows(allowed, schedule.TargetId))
        {
            return (schedule, null);
        }

        return allowed.SelectMany(entry => registered.Where(id => _Matches(entry, id))).FirstOrDefault() is { } target
            ? (schedule with { TargetId = target }, null)
            : (schedule, $"The scheduled backup did not run: {policy.By} allows it only to {string.Join(", ", allowed)}, and none of those is set up here.");
    }

    // A manifest names a kind ("depot", "local"); a registered target is that kind, or one connection of it ("depot:<id>").
    private static bool _Allows(IReadOnlyList<string> allowed, string targetId) => allowed.Any(entry => _Matches(entry, targetId));

    private static bool _Matches(string entry, string targetId) =>
        string.Equals(entry, targetId, StringComparison.OrdinalIgnoreCase)
        || targetId.StartsWith(entry + ":", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(entry, "local", StringComparison.OrdinalIgnoreCase) && targetId == BackupSchedule.LocalFolderTargetId);
}
