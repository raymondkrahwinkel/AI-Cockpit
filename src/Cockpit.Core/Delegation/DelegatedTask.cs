namespace Cockpit.Core.Delegation;

/// <summary>Where a delegated task is in its life (#67).</summary>
public enum DelegatedTaskStatus
{
    /// <summary>Accepted, but the target profile is at its concurrency cap — it starts when a slot frees up.</summary>
    Queued,

    /// <summary>The session is up and working on the prompt.</summary>
    Running,

    /// <summary>The task finished a turn and produced its answer.</summary>
    Completed,

    /// <summary>The session could not start, or the driver reported an error.</summary>
    Failed,

    /// <summary>Stopped on request.</summary>
    Stopped,
}

/// <summary>
/// One unit of delegated work (#67): a prompt handed to another profile, run as a real session with no tab, and
/// watched from the outside. This is the shape the orchestrator's MCP tools report on — deliberately a view over
/// a session rather than a second kind of session.
/// </summary>
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
    string? Error);
