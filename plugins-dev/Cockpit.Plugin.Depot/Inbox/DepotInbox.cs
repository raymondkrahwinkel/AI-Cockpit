using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;

namespace Cockpit.Plugin.Depot.Inbox;

// AC-1586: the inboxes of every configured connection, each its own DepotInboxService with its own hub, so a push on
// one connection only ever refreshes that one. A connection that is removed or changed gets its service closed.
internal sealed class DepotInbox(
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotConnectionRegistration, HttpMessageHandler> handlerFor,
    DepotInboxHubFactory hubFactory,
    Action<DepotInboxPage> publish,
    Microsoft.Extensions.Logging.ILogger? logger = null) : IAsyncDisposable
{
    public const string Scope = "depot.inbox";

    private readonly Dictionary<string, DepotInboxService> _services = new(StringComparer.Ordinal);
    private readonly Lock _servicesLock = new();

    public async Task<IReadOnlyList<DepotInboxCount>> UnreadAsync(CancellationToken cancellationToken)
    {
        var pages = await Task.WhenAll(_Current().Select(service => service.RefreshAsync(cancellationToken))).ConfigureAwait(false);
        return [.. pages.Select(page => page.Count)];
    }

    public Task<DepotInboxPage> ListAsync(DepotInboxRequest request, CancellationToken cancellationToken) =>
        _For(request.ConnectionId).RefreshAsync(cancellationToken);

    public Task<DepotInboxPage> MarkReadAsync(DepotInboxRequest request, CancellationToken cancellationToken) =>
        _For(request.ConnectionId).MarkReadAsync(request.MessageId ?? string.Empty, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        List<DepotInboxService> services;
        lock (_servicesLock)
        {
            services = [.. _services.Values];
            _services.Clear();
        }

        foreach (var service in services)
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }
    }

    private DepotInboxService _For(string connectionId) =>
        _Current().FirstOrDefault(service => service.Connection.Id == connectionId)
        ?? throw new ArgumentException("That Depot connection is no longer configured.", nameof(connectionId));

    // The services for the connections configured now; one whose name or address changed starts over, since its sign-in may be another.
    private List<DepotInboxService> _Current()
    {
        var configured = connections();
        List<DepotInboxService> stale = [];
        List<DepotInboxService> current = [];
        lock (_servicesLock)
        {
            foreach (var (id, service) in _services.ToList())
            {
                if (!configured.Contains(service.Connection))
                {
                    stale.Add(service);
                    _services.Remove(id);
                }
            }

            foreach (var connection in configured)
            {
                if (!_services.TryGetValue(connection.Id, out var service))
                {
                    service = new DepotInboxService(connection, handlerFor(connection), hubFactory, publish, logger);
                    _services[connection.Id] = service;
                }

                current.Add(service);
            }
        }

        foreach (var service in stale)
        {
            _ = service.DisposeAsync().AsTask();
        }

        return current;
    }
}
