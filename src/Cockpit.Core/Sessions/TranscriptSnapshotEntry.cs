namespace Cockpit.Core.Sessions;

// One version of one transcript row, durable enough to rebuild that row after a restart (AC-1090; was AC-684's
// assistant-only `AssistantTranscriptSnapshotEntry`). Not `AssistantTranscriptEntry` (AC-544's read-gateway
// projection, unfit to reconstruct a `ToolUse` chip). Core cannot see Cockpit.App's TranscriptEntryViewModel.

// `Id` is the row's identity, not the line's: a row that changes after it was first written is appended again
// under the same id, and the last version read wins. Positional so no caller can forget it and silently
// duplicate a row per version.

// The optional members below are init-only properties on purpose: one JSON object per line, so a member added
// later reads as its default out of an older build's log and an older build ignores one it does not know.
// `Images` and `IsPendingPermission` (decision points 12 and 13) land here when those are answered.
public sealed record TranscriptSnapshotEntry(
    string Id,
    string Kind,
    string Text,
    string? ToolName,
    string? InputJson,
    string? ToolUseId,
    string? ResultText,
    bool IsResultError,
    DateTimeOffset Timestamp)
{
    // AC-146's nested sub-agent events, each a full entry that can itself nest. Without these a restored
    // Task/Agent row is an empty chip.
    public IReadOnlyList<TranscriptSnapshotEntry>? SubAgentRows { get; init; }

    // What was allowed or denied. Without it a restored conversation is not auditable: the tool call is there,
    // the fact that somebody permitted it is not.
    public string? PermissionDecision { get; init; }

    // AC-720/AC-728: without these a failed turn comes back as an ordinary grey line.
    public SessionErrorKind? ErrorKind { get; init; }

    public DateTimeOffset? RetryAfter { get; init; }

    public bool IsFailedTurnRow { get; init; }

    // AC-935's thread structure, by row id — the live model holds object references, which no file can.
    public string? ReplyToId { get; init; }

    public string? LatestReplyId { get; init; }

    // AC-1056: a background task that outlived the crash is otherwise no longer coupled to the row that started it.
    public string? BackgroundTaskId { get; init; }
}
