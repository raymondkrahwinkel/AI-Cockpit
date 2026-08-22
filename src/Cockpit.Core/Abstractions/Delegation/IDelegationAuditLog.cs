namespace Cockpit.Core.Abstractions.Delegation;

/// <summary>
/// Records what was delegated, to which profile, and how it ended (#67). Delegation runs work under someone's
/// login on a model's say-so, so "what did the agents do while I was away" must outlive the task list. Refusals
/// are recorded too: a log that only holds successes tells you nothing about what was attempted.
/// </summary>
public interface IDelegationAuditLog
{
    /// <summary>Appends an entry. Never throws: an audit failure must not take a delegation down with it, so a broken log is a logged warning rather than a lost task.</summary>
    Task RecordAsync(DelegationAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The most recent entries, newest first, for the audit view.</summary>
    Task<IReadOnlyList<DelegationAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
}

// What happened to a delegated task (#67).
public enum DelegationAuditAction
{
    // A task was accepted and started, or queued for a free slot.
    Delegated,

    // The engine refused: not a target, wrong task type, a working directory it does not allow, recursion, or the profile was at its cap.
    Refused,

    // The task produced its answer.
    Completed,

    // The session failed, or could not start at all.
    Failed,

    // Stopped on request — by the operator, or by the agent that delegated it.
    Stopped,

    // Stopped because it outlived the time the profile allows it.
    TimedOut,

    // Another turn was sent to a task that had already answered.
    FollowUp,

    // The operator approved a per-task request to run above the profile's permission ceiling (AC-117).
    PermissionElevated,

    // The operator was asked to approve a per-task request above the profile's ceiling and declined; the task ran clamped to the ceiling instead (AC-117). Not recorded when there was nobody to ask — that case clamps silently, as it always has.
    PermissionElevationDenied,
}

// One line of the delegation audit trail (#67).
//
// `Prompt`: The prompt, trimmed: enough to recognise the task later without turning the log into a transcript.
// `Reason`: Why a task was refused, or how it failed. Empty for the ordinary path.
public sealed record DelegationAuditEntry(
    DateTimeOffset At,
    DelegationAuditAction Action,
    string ProfileLabel,
    string? TaskId,
    string? Label,
    string? TaskType,
    string? Prompt,
    string? Reason);
