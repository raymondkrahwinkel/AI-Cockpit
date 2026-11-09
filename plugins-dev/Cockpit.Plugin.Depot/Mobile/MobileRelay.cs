using Cockpit.Plugin.Depot.Events;
using Cockpit.Plugin.Depot.Model;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cockpit.Plugin.Depot.Mobile;

// AC-2224: the Cockpit's end of Depot's relay (DEP-629), with the connection's depot.host token. Depot only carries the
// bytes; what a frame means is for MobilePairing to decide.
internal sealed class MobileRelay : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly SemaphoreSlim _sending = new(1, 1);

    public MobileRelay(
        DepotConnectionRegistration connection,
        string userId,
        Func<HttpMessageHandler> handler,
        Action<string, string> opened,
        Action<string, string, byte[]> frame,
        Action<string, string> closed)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(
                $"{connection.Url.TrimEnd('/')}/hubs/relay?expectedUser={Uri.EscapeDataString(userId)}",
                options =>
                {
                    DepotSignalR.Configure(options, handler);
                    options.Transports = HttpTransportType.WebSockets;
                })
            .WithAutomaticReconnect(new _Forever())
            .Build();
        _connection.On("opened", opened);
        _connection.On("frame", frame);
        _connection.On<string, string, string>("closed", (streamId, deviceId, _) => closed(streamId, deviceId));
        _connection.Closed += _ =>
        {
            Closed?.Invoke(this);
            return Task.CompletedTask;
        };
    }

    public event Action<MobileRelay>? Closed;

    public Task StartAsync(CancellationToken cancellationToken) => _connection.StartAsync(cancellationToken);

    // AC-2227: Depot takes one send per connection at a time, and a stream's frames must leave in counter order, so a
    // frame is sealed only once the previous send is done.
    public async Task SendAsync(string streamId, Func<byte[]?> frame, CancellationToken cancellationToken)
    {
        await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (frame() is { } sealedFrame)
            {
                await _connection.InvokeAsync("Send", streamId, sealedFrame, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sending.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _sending.Dispose();
    }

    // A phone may come back at any time, so the relay keeps retrying at the events hub's slowest step.
    private sealed class _Forever : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            DepotEventReconnect.Backoff[(int)Math.Min(retryContext.PreviousRetryCount, DepotEventReconnect.Backoff.Length - 1)];
    }
}
