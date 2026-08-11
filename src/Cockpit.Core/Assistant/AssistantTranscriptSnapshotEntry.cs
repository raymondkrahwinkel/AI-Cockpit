namespace Cockpit.Core.Assistant;

// One row of the assistant's transcript, durable enough to redraw the window after a resume (AC-684).
// Not `AssistantTranscriptEntry` (AC-544's read-gateway projection, unfit to reconstruct a `ToolUse` chip).
// Provider-neutral and decoupled from Cockpit.App's TranscriptEntryViewModel — Core cannot see that assembly.
public sealed record AssistantTranscriptSnapshotEntry(
    string Kind,
    string Text,
    string? ToolName,
    string? InputJson,
    string? ToolUseId,
    string? ResultText,
    bool IsResultError,
    DateTimeOffset Timestamp);
