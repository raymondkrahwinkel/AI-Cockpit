using Cockpit.Plugin.Depot.Model;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Events;

internal sealed class DepotEvents(
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotConnectionRegistration, HttpMessageHandler> handlerFor,
    DepotEventHubFactory hubFactory,
    ILogger? logger = null) : IAsyncDisposable
{
    private readonly Dictionary<string, DepotEventSubscriber> _subscribers = new(StringComparer.Ordinal);
    private readonly Lock _subscribersLock = new();

    public Task<IDisposable> SubscribeAsync(string connectionId, string topic, Action<DepotEvent> onEvent, Action resync, CancellationToken cancellationToken)
    {
        var connection = connections().FirstOrDefault(candidate => candidate.Id == connectionId)
            ?? throw new ArgumentException("That Depot connection is no longer configured.", nameof(connectionId));
        return _For(connection).SubscribeAsync(topic, onEvent, resync, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        List<DepotEventSubscriber> subscribers;
        lock (_subscribersLock)
        {
            subscribers = [.. _subscribers.Values];
            _subscribers.Clear();
        }

        foreach (var subscriber in subscribers)
        {
            await subscriber.DisposeAsync().ConfigureAwait(false);
        }
    }

    private DepotEventSubscriber _For(DepotConnectionRegistration connection)
    {
        DepotEventSubscriber? stale = null;
        DepotEventSubscriber subscriber;
        lock (_subscribersLock)
        {
            if (_subscribers.TryGetValue(connection.Id, out var existing))
            {
                if (existing.Connection == connection)
                {
                    return existing;
                }

                stale = existing;
            }

            subscriber = new DepotEventSubscriber(connection, () => handlerFor(connection), hubFactory, logger);
            _subscribers[connection.Id] = subscriber;
        }

        _ = stale?.DisposeAsync().AsTask();
        return subscriber;
    }
}
