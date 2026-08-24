namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The append-only trail of every session an agent asked the host to start or stop (AC-545, criterion 5), on the shared
/// <c>JsonlAuditLog&lt;T&gt;</c> machinery like <c>IConsentAuditLog</c>/<c>IDelegationAuditLog</c>. Answers "what has this
/// ever started" across restarts, unlike the transcript's single conversation — a refused spawn is recorded too, so the gate shows what it stopped, not only what it let through.
/// </summary>
public interface IAssistantSpawnAuditLog
{
    /// <summary>
    /// Appends one entry. Never throws: losing the record is bad, failing the operator's approved action because of it is worse.
    /// </summary>
    Task RecordAsync(AssistantSpawnAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent entries, newest first.
    /// </summary>
    Task<IReadOnlyList<AssistantSpawnAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
}

// AC-1013: One line of the trail. Criterion 5's four required fields (caller, target workspace, profile,
// working directory) are each their own field, not folded into a sentence, so the trail stays greppable and
// columnar. ProjectId (AC-773) records which route (explicit or folder map-match) supplied the project.
public sealed record AssistantSpawnAuditEntry(
    DateTimeOffset At,
    AssistantSpawnAction Action,
    SpawnCaller Caller,
    string? CallerPaneId,
    string WorkspaceId,
    string? WorkspaceName,
    string? Profile,
    string? WorkingDirectory,
    string? PaneId,
    string? SessionName,
    string? Refusal,
    string? ProjectId = null);

// What a trail entry records having been asked for.
public enum AssistantSpawnAction
{
    Start,
    Stop,

    // A turn submitted into a session the assistant did not start — the hand-off `send_prompt` makes.
    Prompt,

    // A worktree re-owned from the assistant onto a session — `worktree_handover` (AC-719).
    Handover,
}
