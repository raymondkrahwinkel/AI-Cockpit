namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The append-only trail of every session an agent asked the host to start or stop (AC-545, criterion 5), on the shared
/// <c>JsonlAuditLog&lt;T&gt;</c> machinery like <c>IConsentAuditLog</c>/<c>IDelegationAuditLog</c>. Answers "what has this
/// ever started" across restarts, unlike the transcript's single conversation — a refused spawn is recorded too, so the gate shows what it stopped, not only what it let through.
/// </summary>
public interface IAssistantSpawnAuditLog
{
    /// <summary>Appends one entry. Never throws: losing the record is bad, failing the operator's approved action because of it is worse.</summary>
    Task RecordAsync(AssistantSpawnAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The most recent entries, newest first.</summary>
    Task<IReadOnlyList<AssistantSpawnAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
}

// One line of the trail. Criterion 5 names four things it must carry — caller, target workspace, profile and
// working directory — and each is here as its own field rather than folded into a sentence, so the trail stays
// something you can grep and the window can lay out in columns.
//
// `At`: When it happened.
// `Action`: What was asked for: `AssistantSpawnAction.Start` or `AssistantSpawnAction.Stop`.
// `Caller`: Which authority asked — the assistant, or a coordinator (AC-436). See `SpawnTarget`.
// `CallerPaneId`: The verified pane of a host-derived caller; null for the assistant, which has none.
// `WorkspaceId`: The desk it landed on (or would have).
// `WorkspaceName`: That desk's label at the time, kept because a workspace can be renamed or closed and the id then names nothing a reader recognises.
// `Profile`: The profile the session runs under — the field that says what it costs.
// `WorkingDirectory`: The folder it was started in, or null when the profile's default was used.
// `PaneId`: The pane that resulted, or null for a refusal.
// `SessionName`: What the pane is called, so the trail is readable without cross-referencing pane ids.
// `Refusal`: Why it did not happen, or null when it did. A trail without its refusals hides the gate working.
// `ProjectId` (AC-773): The project a start resolved to, by id — via `AgentSpawnRequest.ProjectId` or the folder's
// own map-match, whichever supplied it — or null when neither did. Recorded so the trail can tell which route a
// project came in by, not only that BehaviorPrompt/isolation/etc. were applied.
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
