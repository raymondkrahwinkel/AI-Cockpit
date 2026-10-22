namespace Cockpit.Plugin.Schedules.Contracts;

// A scheduled task: a schedule in a zone, what to do, and where the result goes (plan §5). The zone is required:
// an empty one was the UTC gap of AC-1361, so a task without a zone this machine knows never fires.
public sealed record ScheduledTask
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    // A 5-field cron, `once yyyy-MM-dd HH:mm` or `every N m`.
    public required string Schedule { get; init; }

    // An IANA zone such as Europe/Amsterdam.
    public required string TimeZone { get; init; }

    public MissedRunPolicy MissedRuns { get; init; } = MissedRunPolicy.RunOnceWhenBack;

    public bool IsEnabled { get; init; } = true;

    // The operator's say-so that a scheduled run may change files without asking first (plan §3).
    public bool RunUnattended { get; init; }

    public required TaskAction Action { get; init; }

    public IReadOnlyList<DeliveryTarget> Delivery { get; init; } = [];
}
