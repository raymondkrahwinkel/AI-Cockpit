namespace Cockpit.Core.Abstractions.Delegation;

/// <summary>
/// Records what was delegated, to which profile, and how it ended (#67). Delegation runs work under someone's
/// login on a model's say-so, so "what did the agents actually do while I was away" has to be answerable from
/// something more durable than the task list, which lives only as long as the app does.
/// </summary>
/// <remarks>
/// Refusals are recorded too, not just what ran: a guard that turned something away is exactly what you want to
/// see afterwards, and a log that only holds successes tells you nothing about what was attempted.
/// </remarks>
public interface IDelegationAuditLog
{
    /// <summary>Appends an entry. Never throws: an audit failure must not take a delegation down with it, so a broken log is a logged warning rather than a lost task.</summary>
    Task RecordAsync(DelegationAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The most recent entries, newest first, for the audit view.</summary>
    Task<IReadOnlyList<DelegationAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
}

/// <summary>What happened to a delegated task (#67).</summary>
public enum DelegationAuditAction
{
    /// <summary>A task was accepted and started, or queued for a free slot.</summary>
    Delegated,

    /// <summary>The engine refused: not a target, wrong task type, a working directory it does not allow, recursion, or the profile was at its cap.</summary>
    Refused,

    /// <summary>The task produced its answer.</summary>
    Completed,

    /// <summary>The session failed, or could not start at all.</summary>
    Failed,

    /// <summary>Stopped on request — by the operator, or by the agent that delegated it.</summary>
    Stopped,

    /// <summary>Stopped because it outlived the time the profile allows it.</summary>
    TimedOut,

    /// <summary>Another turn was sent to a task that had already answered.</summary>
    FollowUp,
}

/// <summary>One line of the delegation audit trail (#67).</summary>
public sealed record DelegationAuditEntry(
    DateTimeOffset At,
    DelegationAuditAction Action,
    string ProfileLabel,
    string? TaskId,
    string? Label,
    string? TaskType,
    /// <summary>The prompt, trimmed: enough to recognise the task later without turning the log into a transcript.</summary>
    string? Prompt,
    /// <summary>Why a task was refused, or how it failed. Empty for the ordinary path.</summary>
    string? Reason);
