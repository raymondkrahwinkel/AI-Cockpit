using System.Net;
using System.Net.Http.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Events;

internal sealed class DepotEventSubscriber : IAsyncDisposable
{
    private readonly DepotConnectionRegistration _connection;
    private readonly HttpClient _http;
    private readonly DepotEventHubFactory _hubFactory;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _subscriptionsLock = new();
    private readonly Dictionary<string, List<_Consumer>> _subscriptions = new(StringComparer.Ordinal);
    private IDepotEventHub? _hub;
    private long _version;
    private int _attempt;

    public DepotEventSubscriber(
        DepotConnectionRegistration connection,
        Func<HttpMessageHandler> handler,
        DepotEventHubFactory hubFactory,
        ILogger? logger = null)
    {
        _connection = connection;
        _http = new HttpClient(handler()) { Timeout = TimeSpan.FromSeconds(30) };
        _hubFactory = hubFactory;
        _logger = logger;
    }

    public DepotConnectionRegistration Connection => _connection;

    public async Task<IDisposable> SubscribeAsync(string topic, Action<DepotEvent> onEvent, Action resync, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(onEvent);
        ArgumentNullException.ThrowIfNull(resync);

        var consumer = new _Consumer(onEvent, resync);
        var subscribe = false;
        lock (_subscriptionsLock)
        {
            if (!_subscriptions.TryGetValue(topic, out var consumers))
            {
                consumers = [];
                _subscriptions[topic] = consumers;
                subscribe = true;
            }

            consumers.Add(consumer);
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var wasConnected = _hub is not null;
                var hub = await _EnsureHubAsync(cancellationToken).ConfigureAwait(false);
                if (hub is not null && subscribe && wasConnected)
                {
                    await hub.SubscribeAsync(topic, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // A subscribe that failed or was cancelled hands back no registration to dispose, so its consumer goes now.
            _Remove(topic, consumer);
            throw;
        }

        return new _Registration(() => _Remove(topic, consumer));
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        await _CloseHubAsync().ConfigureAwait(false);
        _http.Dispose();
        _life.Dispose();
    }

    private async Task<IDepotEventHub?> _EnsureHubAsync(CancellationToken cancellationToken)
    {
        if (_hub is not null)
        {
            return _hub;
        }

        var user = await _UserAsync(cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        var hub = _hubFactory(_connection, user, _OnEvent, _OnReconnectedAsync, _OnClosedAsync);
        try
        {
            await hub.StartAsync(cancellationToken).ConfigureAwait(false);
            foreach (var topic in _Topics())
            {
                await hub.SubscribeAsync(topic, cancellationToken).ConfigureAwait(false);
            }

            _hub = hub;
            _attempt = 0;
            return hub;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
            _logger?.LogWarning(exception, "Connecting Depot events for {Connection} failed.", _connection.Name);
            _RetryLater();
            return null;
        }
    }

    private async Task<string?> _UserAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync($"{_connection.Url.TrimEnd('/')}/cockpit/me", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger?.LogInformation("Depot events are unavailable for {Connection} because this Depot is older than DEP-428.", _connection.Name);
                return null;
            }

            response.EnsureSuccessStatusCode();
            var me = await response.Content.ReadFromJsonAsync<_Me>(DepotChannel.Json, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(me?.UserId))
            {
                _logger?.LogWarning("Depot did not identify the user for events on {Connection}.", _connection.Name);
                return null;
            }

            return me.UserId;
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger?.LogWarning(exception, "Reading the Depot event identity for {Connection} failed.", _connection.Name);
            _RetryLater();
            return null;
        }
    }

    private void _OnEvent(DepotEvent cockpitEvent)
    {
        if (_version != 0 && cockpitEvent.Version != _version + 1)
        {
            _Resync(cockpitEvent.Topic);
        }

        _version = cockpitEvent.Version;
        _Publish(cockpitEvent);
    }

    private async Task _OnReconnectedAsync()
    {
        _version = 0;
        var hub = _hub;
        if (hub is not null)
        {
            foreach (var topic in _Topics())
            {
                await hub.SubscribeAsync(topic, _life.Token).ConfigureAwait(false);
            }
        }

        _ResyncAll();
    }

    private Task _OnClosedAsync(Exception? exception)
    {
        _hub = null;
        _version = 0;
        _RetryLater();
        return Task.CompletedTask;
    }

    private async Task _CloseHubAsync()
    {
        var hub = Interlocked.Exchange(ref _hub, null);
        if (hub is not null)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void _RetryLater()
    {
        if (_life.IsCancellationRequested)
        {
            return;
        }

        var delay = DepotEventReconnect.Backoff[Math.Min(_attempt++, DepotEventReconnect.Backoff.Length - 1)];
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _life.Token).ConfigureAwait(false);
                await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
                try
                {
                    if (_Topics().Count > 0)
                    {
                        await _EnsureHubAsync(_life.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Reconnecting Depot events for {Connection} failed.", _connection.Name);
            }
        });
    }

    private void _Publish(DepotEvent cockpitEvent)
    {
        foreach (var consumer in _Consumers(cockpitEvent.Topic))
        {
            consumer.OnEvent(cockpitEvent);
        }
    }

    private void _Resync(string topic)
    {
        foreach (var consumer in _Consumers(topic))
        {
            consumer.Resync();
        }
    }

    private void _ResyncAll()
    {
        foreach (var topic in _Topics())
        {
            _Resync(topic);
        }
    }

    private List<_Consumer> _Consumers(string topic)
    {
        lock (_subscriptionsLock)
        {
            return _subscriptions.TryGetValue(topic, out var consumers) ? [.. consumers] : [];
        }
    }

    private List<string> _Topics()
    {
        lock (_subscriptionsLock)
        {
            return [.. _subscriptions.Keys];
        }
    }

    private void _Remove(string topic, _Consumer consumer)
    {
        lock (_subscriptionsLock)
        {
            if (!_subscriptions.TryGetValue(topic, out var consumers))
            {
                return;
            }

            consumers.Remove(consumer);
            if (consumers.Count == 0)
            {
                _subscriptions.Remove(topic);
            }
        }
    }

    private sealed record _Me(string? UserId);

    private sealed record _Consumer(Action<DepotEvent> OnEvent, Action Resync);

    private sealed class _Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
