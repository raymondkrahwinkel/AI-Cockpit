using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;

namespace Cockpit.App.Composition;

internal sealed class RemoteWindowConversations(Func<ISessionLauncher?> current) : IConversationIndex
{
    private IConversationIndex _Index => current() as IConversationIndex
        ?? throw new InvalidOperationException("The server's conversations are not connected yet.");

    public Task<IReadOnlyList<ConversationRecord>> LoadAsync(CancellationToken cancellationToken = default) => _Index.LoadAsync(cancellationToken);

    public Task<IReadOnlyList<ConversationSearchResult>> SearchAsync(string text, string? projectId = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken cancellationToken = default) =>
        _Index.SearchAsync(text, projectId, from, to, cancellationToken);

    public Task RecordAsync(ConversationRecord record, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The server records its own conversations.");
}
