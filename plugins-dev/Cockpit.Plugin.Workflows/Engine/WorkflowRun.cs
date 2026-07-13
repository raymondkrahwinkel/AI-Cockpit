namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// One execution of a flow (#69) — what happened, step by step. Kept, not thrown away: "it did not work" is not
/// something an operator can act on, and the only way to answer "why" is to have written down what each step got,
/// what it produced and how long it took.
/// </summary>
public sealed class WorkflowRun
{
    public required string Id { get; init; }

    public required string WorkflowId { get; init; }

    public required string WorkflowName { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>Why the run failed, when it did — the message the operator reads.</summary>
    public string? Error { get; set; }

    public List<StepRun> Steps { get; init; } = [];

    public TimeSpan Duration => (FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}
