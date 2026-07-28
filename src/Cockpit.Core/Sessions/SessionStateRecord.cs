namespace Cockpit.Core.Sessions;

/// <summary>
/// Enough about one pane's session to bring it back after a restart or a crash (AC-409) — appended to
/// <c>session-state.jsonl</c> next to <c>cockpit.json</c> every time any of these fields changes, never on
/// shutdown: a write that only happens on a clean exit is exactly the one a crash never reaches, which is the
/// failure mode this record exists for.
/// <para>
/// Deliberately thin — resuming a session and any repair UI for it are a follow-up ticket's concern, not this
/// one's, and every field kept here beyond what that needs is one more thing that can be stale between when it
/// was written and when it is read back after a crash.
/// </para>
/// </summary>
/// <param name="PaneId">Which pane this record describes — the key the store keeps the latest record per.</param>
/// <param name="ProfileId">The profile's label (a <see cref="Profiles.SessionProfile"/> has no separate id), once known.</param>
/// <param name="ProviderId">Which backend the profile runs under — a plugin provider's registered id, else the built-in <see cref="Profiles.SessionProvider"/>'s name.</param>
/// <param name="ConversationId">The provider's own conversation id (AC-408's <see cref="SessionConversationId.Value"/>), once reported.</param>
/// <param name="ConversationState">Whether the conversation id is known yet, and if not, why (AC-408's <see cref="SessionConversationId.State"/>).</param>
/// <param name="WorkingDirectory">The directory the session actually runs in — the isolated worktree's path when isolation applied, else the folder as given.</param>
/// <param name="WorktreePath">The isolated worktree's own path, when this session runs in one; null when it runs in its working directory as given.</param>
/// <param name="WorktreeBranch">The worktree's branch (AC-85), when this session runs in one.</param>
/// <param name="PermissionMode">The Claude CLI <c>--permission-mode</c> value this session was launched with, or last live-switched to.</param>
/// <param name="RecordedAt">When this record was written.</param>
public sealed record SessionStateRecord(
    string PaneId,
    string? ProfileId,
    string? ProviderId,
    string? ConversationId,
    SessionConversationIdState ConversationState,
    string? WorkingDirectory,
    string? WorktreePath,
    string? WorktreeBranch,
    string? PermissionMode,
    DateTimeOffset RecordedAt);
