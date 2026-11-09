using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Contracts;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Events;

internal sealed class DepotEvents(
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotConnectionRegistration, HttpMessageHandler> handlerFor,
    DepotEventHubFactory hubFactory,
    ILogger? logger = null,
    Action<DepotConnectionRegistration>? connected = null) : IAsyncDisposable
{
    private readonly Dictionary<string, DepotEventSubscriber> _subscribers = new(StringComparer.Ordinal);
    private readonly Lock _subscribersLock = new();

    public DepotRegistrationStatus? Registration(string connectionId)
    {
        lock (_subscribersLock)
        {
            return _subscribers.TryGetValue(connectionId, out var subscriber) ? subscriber.Registration : null;
        }
    }

    public async Task RefreshAsync(string? serverName = null)
    {
        var current = connections();
        List<DepotEventSubscriber> removed;
        List<DepotEventSubscriber> reset;
        lock (_subscribersLock)
        {
            removed = _subscribers.Values.Where(subscriber => !current.Contains(subscriber.Connection)).ToList();
            reset = _subscribers.Values.Where(subscriber => current.Contains(subscriber.Connection) && subscriber.Connection.McpServerName == serverName).ToList();
            foreach (var subscriber in removed)
            {
                _subscribers.Remove(subscriber.Connection.Id);
            }
            foreach (var connection in current)
            {
                _For(connection);
            }
        }
        foreach (var subscriber in reset)
        {
            await subscriber.ResetAsync().ConfigureAwait(false);
        }
        foreach (var subscriber in removed)
        {
            await subscriber.DisposeAsync().ConfigureAwait(false);
        }
    }

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

            subscriber = new DepotEventSubscriber(connection, () => handlerFor(connection), hubFactory, logger, connected);
            _subscribers[connection.Id] = subscriber;
        }

        if (stale is not null)
        {
            _ = stale.DisposeAsync().AsTask().ContinueWith(
                closing => logger?.LogWarning(closing.Exception?.GetBaseException(), "Closing stale Depot events for {Connection} failed.", stale.Connection.Name),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        return subscriber;
    }
}
