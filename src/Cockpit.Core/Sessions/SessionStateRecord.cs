namespace Cockpit.Core.Sessions;

// Enough about one pane's session to bring it back after a restart or a crash (AC-409) — appended to
// `session-state.jsonl` next to `cockpit.json` every time any of these fields changes, never on
// shutdown: a write that only happens on a clean exit is exactly the one a crash never reaches, which is the
// failure mode this record exists for.
//
// Deliberately thin — resuming a session and any repair UI for it are a follow-up ticket's concern, not this
// one's, and every field kept here beyond what that needs is one more thing that can be stale between when it
// was written and when it is read back after a crash.
//
// `PaneId`: Which pane this record describes — the key the store keeps the latest record per.
// `ProfileId`: The profile's label (a `Profiles.SessionProfile` has no separate id), once known.
// `ProviderId`: Which backend the profile runs under — a plugin provider's registered id, else the built-in `Profiles.SessionProvider`'s name.
// `ConversationId`: The provider's own conversation id (AC-408's `SessionConversationId.Value`), once reported.
// `ConversationState`: Whether the conversation id is known yet, and if not, why (AC-408's `SessionConversationId.State`).
// `WorkingDirectory`: The directory the session actually runs in — the isolated worktree's path when isolation applied, else the folder as given.
// `WorktreePath`: The isolated worktree's own path, when this session runs in one; null when it runs in its working directory as given.
// `WorktreeBranch`: The worktree's branch (AC-85), when this session runs in one.
// `PermissionMode`: The Claude CLI `--permission-mode` value this session was launched with, or last live-switched to.
// `RecordedAt`: When this record was written.
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
