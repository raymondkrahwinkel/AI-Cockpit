namespace Cockpit.Plugin.Schedules.Contracts;

// One run, or one slot that did not run, in a task's history.
public sealed record RunRecord
{
    public required string Id { get; init; }

    public required string TaskId { get; init; }

    // The slot this run belongs to; null for a run nobody scheduled.
    public DateTimeOffset? ScheduledFor { get; init; }

    public required RunTrigger Trigger { get; init; }

    public required RunOutcome Outcome { get; init; }

    // Why it failed, was skipped or missed: "interrupted", "still running", the error.
    public string? Reason { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public TimeSpan Duration { get; init; }

    public string? Output { get; init; }
}
