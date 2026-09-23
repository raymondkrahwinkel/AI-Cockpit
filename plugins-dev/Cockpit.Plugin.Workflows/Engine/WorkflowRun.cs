namespace Cockpit.Plugin.Workflows.Engine;

// One execution of a flow (#69) — what happened, step by step. Kept, not thrown away: "it did not work" is not
// something an operator can act on, and the only way to answer "why" is to have written down what each step got,
// what it produced and how long it took.
public sealed class WorkflowRun
{
    public required string Id { get; init; }

    public required string WorkflowId { get; init; }

    public required string WorkflowName { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    // Why the run failed, when it did — the message the operator reads.
    public string? Error { get; set; }

    // A note about the run itself rather than any one step — a schedule catching up late, or not running at all
    // because a slot fell outside the grace (#AC-1359). Separate from `Error`: a late run still succeeded.
    public string? Note { get; set; }

    public List<StepRun> Steps { get; init; } = [];

    public TimeSpan Duration => (FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}
