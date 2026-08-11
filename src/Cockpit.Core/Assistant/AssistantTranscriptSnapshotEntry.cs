namespace Cockpit.Core.Assistant;

// One row of the assistant's transcript, durable enough to redraw the window after a resume (AC-684). Not
// `AssistantTranscriptEntry` (Abstractions.Assistant, AC-544's read-gateway): that one is a deliberately thin,
// three-field prose projection for the assistant's own read tool, built from a live session and unfit to
// reconstruct a `ToolUse` row's chip (its tool name and input are already merged into `Text`). This one keeps
// them apart so a restored row renders the same as a live one.
//
// Provider-neutral and decoupled from Cockpit.App's TranscriptEntryViewModel on purpose — Core cannot see that
// assembly, and the mapping between the two lives on the App side, next to the view model itself.
//
// Deliberately thinner than the view model: no pending-permission state (a decision the dead process can no
// longer act on), no reading-level/grouping fields (recomputed live from Kind), and no sub-agent nesting
// (flattens on restore — see AssistantSessionHost's own note on that ceiling).
//
// `Kind`: TranscriptEntryKind's name, as a string so this type does not have to know that enum.
public sealed record AssistantTranscriptSnapshotEntry(
    string Kind,
    string Text,
    string? ToolName,
    string? InputJson,
    string? ToolUseId,
    string? ResultText,
    bool IsResultError,
    DateTimeOffset Timestamp);
