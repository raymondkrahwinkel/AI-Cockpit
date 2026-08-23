namespace Cockpit.Core.Sessions;

// AC-409: enough about one pane's session to bring it back after a restart or a crash — appended to
// `session-state.jsonl` on every change, never on shutdown, since a clean-exit-only write is exactly what a
// crash never reaches. Deliberately thin: resume and repair UI are a follow-up ticket's concern, not this one's.
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
