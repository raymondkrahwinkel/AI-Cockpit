namespace Cockpit.Core.Sessions;

// Durable metadata for one local conversation. Every change is appended; the latest PaneId row is authoritative.
public sealed record ConversationRecord(
    string PaneId,
    string Title,
    string? ProjectId,
    string? WorkingDirectory,
    string? ProfileId,
    string? ProviderId,
    string? ConversationId,
    SessionConversationIdState ConversationState,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    string? LastStatus,
    DateTimeOffset? ClosedAt,
    bool StartedByTheAssistant,
    bool IsAssistant,
    string? TranscriptPath);

public sealed record ConversationSearchResult(ConversationRecord Record, string? Fragment);
