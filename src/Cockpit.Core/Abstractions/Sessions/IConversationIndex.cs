using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

public interface IConversationIndex
{
    Task RecordAsync(ConversationRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationRecord>> LoadAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationSearchResult>> SearchAsync(
        string text,
        string? projectId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default);
}
