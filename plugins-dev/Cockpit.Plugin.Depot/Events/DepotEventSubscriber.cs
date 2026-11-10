using System.Net;
using System.Net.Http.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Events;

internal sealed class DepotEventSubscriber : IAsyncDisposable
{
    // Depot has no "devices" subscription: it sends device events per listener of the user's valid grant, whatever that listener subscribed to (DEP-628), so "projects" is the subscription that carries them.
    public const string DevicesTopic = "devices";
    private const string ProjectsTopic = "projects";

    private readonly DepotConnectionRegistration _connection;
    private Lazy<HttpClient> _http;
    private readonly Func<HttpMessageHandler> _handler;
    private readonly DepotEventHubFactory _hubFactory;
    private readonly ILogger? _logger;

    // AC-2217: told each time the hub is up again, first start and reconnect alike.
    private readonly Action<DepotConnectionRegistration>? _connected;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _subscriptionsLock = new();
    private readonly Dictionary<string, List<_Consumer>> _subscriptions = new(StringComparer.Ordinal);
    private IDepotEventHub? _hub;
    private long _version;
    private int _attempt;
    private int _failures;
    private readonly Task _heartbeat;
    private DepotRegistrationStatus? _registration;
    private int _disposed;
    private int _retryScheduled;
    private Task _retry = Task.CompletedTask;

    public DepotRegistrationStatus? Registration => Volatile.Read(ref _registration);

    public DepotEventSubscriber(
        DepotConnectionRegistration connection,
        Func<HttpMessageHandler> handler,
        DepotEventHubFactory hubFactory,
        ILogger? logger = null,
        Action<DepotConnectionRegistration>? connected = null)
    {
        _connected = connected;
        _connection = connection;
        _handler = handler;
        _http = new Lazy<HttpClient>(_CreateHttpClient);
        _hubFactory = hubFactory;
        _logger = logger;
        _heartbeat = Task.Run(_HeartbeatLoopAsync);
        _RetryLater();
    }

    public DepotConnectionRegistration Connection => _connection;

    private HttpClient _CreateHttpClient() => new(_handler()) { Timeout = TimeSpan.FromSeconds(30) };

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
                    await hub.SubscribeAsync(_HubTopic(topic), cancellationToken).ConfigureAwait(false);
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

    public async Task ResetAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        try
        {
            await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                await _CloseHubAsync().ConfigureAwait(false);
                if (_http.IsValueCreated)
                {
                    _http.Value.Dispose();
                }
                _http = new Lazy<HttpClient>(_CreateHttpClient);
                _version = 0;
                Volatile.Write(ref _registration, null);
                _RetryLater();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException && Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _life.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_heartbeat, _retry).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _CloseHubAsync().ConfigureAwait(false);
            }
            finally
            {
                if (_http.IsValueCreated)
                {
                    _http.Value.Dispose();
                }
                _life.Dispose();
            }
        }
    }

    private async Task _HeartbeatLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                try
                {
                    if (Volatile.Read(ref _hub) is { IsConnected: true } hub)
                    {
                        await _HeartbeatAsync(hub).ConfigureAwait(false);
                    }
                    else
                    {
                        _HeartbeatFailed();
                    }
                }
                catch (Exception exception) when (!_life.IsCancellationRequested)
                {
                    _HeartbeatFailed();
                    _logger?.LogDebug(exception, "Depot heartbeat failed for {Connection}.", _connection.Name);
                }
            }
            while (await timer.WaitForNextTickAsync(_life.Token).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "Depot heartbeat stopped for {Connection}.", _connection.Name);
        }
    }

    private async Task _HeartbeatAsync(IDepotEventHub hub)
    {
        await hub.HeartbeatAsync(_life.Token).ConfigureAwait(false);
        var me = await _UserAsync(_life.Token).ConfigureAwait(false);
        if (me is not null && ReferenceEquals(hub, Volatile.Read(ref _hub)))
        {
            Interlocked.Exchange(ref _failures, 0);
            Volatile.Write(ref _registration, new(me.Installation?.Name, true));
        }
    }

    private void _HeartbeatFailed()
    {
        if (Interlocked.Increment(ref _failures) >= 3 && Registration is { } registration)
        {
            Volatile.Write(ref _registration, registration with { Online = false });
        }
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

        var hub = _hubFactory(_connection, user.UserId, _OnEvent, _OnReconnectedAsync, _OnClosedAsync);
        try
        {
            await hub.StartAsync(cancellationToken).ConfigureAwait(false);
            foreach (var topic in _HubTopics())
            {
                await hub.SubscribeAsync(topic, cancellationToken).ConfigureAwait(false);
            }

            _hub = hub;
            _attempt = 0;
            _connected?.Invoke(_connection);
            return hub;
        }
        catch (Exception exception)
        {
            await hub.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _logger?.LogWarning(exception, "Connecting Depot events for {Connection} failed.", _connection.Name);
            return null;
        }
    }

    private async Task<_Me?> _UserAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.Value.GetAsync($"{_connection.Url.TrimEnd('/')}/cockpit/me", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger?.LogInformation("Depot events are unavailable for {Connection} because this Depot is older than DEP-428.", _connection.Name);
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Volatile.Write(ref _registration, null);
                _logger?.LogDebug("Depot registration has no valid token for {Connection}.", _connection.Name);
                return null;
            }
            response.EnsureSuccessStatusCode();
            var me = await response.Content.ReadFromJsonAsync<_Me>(DepotChannel.Json, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(me?.UserId))
            {
                _logger?.LogWarning("Depot did not identify the user for events on {Connection}.", _connection.Name);
                return null;
            }

            return me;
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger?.LogDebug(exception, "Reading the Depot event identity for {Connection} failed.", _connection.Name);
            return null;
        }
    }

    private void _OnEvent(DepotEvent cockpitEvent)
    {
        if (_version != 0 && cockpitEvent.Version != _version + 1)
        {
            // One counter covers every topic, so a gap may have been an event of any of them.
            _ResyncAll();
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
            foreach (var topic in _HubTopics())
            {
                await hub.SubscribeAsync(topic, _life.Token).ConfigureAwait(false);
            }
        }

        _ResyncAll();
        if (hub is not null)
        {
            try
            {
                await _HeartbeatAsync(hub).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _HeartbeatFailed();
                _logger?.LogDebug(exception, "Depot reconnect heartbeat failed for {Connection}.", _connection.Name);
            }
        }
        _connected?.Invoke(_connection);
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
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _hub) is not null || Interlocked.CompareExchange(ref _retryScheduled, 1, 0) != 0)
        {
            return;
        }
        _retry = Task.Run(async () =>
        {
            try
            {
                while (!_life.IsCancellationRequested && Volatile.Read(ref _hub) is null)
                {
                    var delay = DepotEventReconnect.Backoff[_attempt];
                    _attempt = Math.Min(_attempt + 1, DepotEventReconnect.Backoff.Length - 1);
                    await Task.Delay(delay, _life.Token).ConfigureAwait(false);
                    _logger?.Log(Registration is null ? LogLevel.Debug : LogLevel.Information, "Reconnecting Depot events for {Connection} (attempt {Attempt}).", _connection.Name, _attempt);
                    IDepotEventHub? hub;
                    await _gate.WaitAsync(_life.Token).ConfigureAwait(false);
                    try
                    {
                        hub = await _EnsureHubAsync(_life.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                    if (hub is { IsConnected: true })
                    {
                        await _HeartbeatAsync(hub).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception exception)
            {
                _logger?.LogDebug(exception, "Depot reconnect stopped for {Connection}.", _connection.Name);
            }
            finally
            {
                Interlocked.Exchange(ref _retryScheduled, 0);
                _RetryLater();
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

    private List<string> _HubTopics() => [.. _Topics().Select(_HubTopic).Distinct()];

    private static string _HubTopic(string topic) => topic == DevicesTopic ? ProjectsTopic : topic;

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

    private sealed record _Me(string UserId, _Installation? Installation);

    private sealed record _Installation(string? Name);

    private sealed record _Consumer(Action<DepotEvent> OnEvent, Action Resync);

    private sealed class _Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
