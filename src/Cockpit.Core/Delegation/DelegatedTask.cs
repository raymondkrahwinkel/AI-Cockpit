namespace Cockpit.Core.Delegation;

// Where a delegated task is in its life (#67).
public enum DelegatedTaskStatus
{
    // Accepted, but the target profile is at its concurrency cap — it starts when a slot frees up.
    Queued,

    // The session is up and working on the prompt.
    Running,

    // The task finished a turn and produced its answer.
    Completed,

    // The session could not start, or the driver reported an error.
    Failed,

    // Stopped on request.
    Stopped,
}

// One unit of delegated work (#67): a prompt handed to another profile, run as a real session with no tab, and
// watched from the outside. This is the shape the orchestrator's MCP tools report on — deliberately a view over
// a session rather than a second kind of session.
public sealed record DelegatedTaskView(
    string TaskId,
    string ProfileLabel,
    string? Label,
    string? TaskType,
    DelegatedTaskStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int TurnCount,
    string? Result,
    string? Error,
    // The verified pane that created the task, or null off the verified path. Carried so an unscoped read
    // (AC-641's list_delegated_tasks) can attribute background work to the session that started it; a scoped
    // caller only ever sees its own pane here.
    string? OwnerPaneId = null);
