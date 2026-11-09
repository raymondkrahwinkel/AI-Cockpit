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

    public MobileRelay(
        DepotConnectionRegistration connection,
        string userId,
        Func<HttpMessageHandler> handler,
        Action<string, string> opened,
        Action<string, byte[]> frame,
        Action<string> closed)
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
        _connection.On<string, string>("closed", (deviceId, _) => closed(deviceId));
        _connection.Closed += _ =>
        {
            Closed?.Invoke(this);
            return Task.CompletedTask;
        };
    }

    public event Action<MobileRelay>? Closed;

    public Task StartAsync(CancellationToken cancellationToken) => _connection.StartAsync(cancellationToken);

    public Task SendAsync(string deviceId, byte[] frame, CancellationToken cancellationToken) =>
        _connection.InvokeAsync("Send", deviceId, frame, cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    // A phone may come back at any time, so the relay keeps retrying at the events hub's slowest step.
    private sealed class _Forever : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            DepotEventReconnect.Backoff[(int)Math.Min(retryContext.PreviousRetryCount, DepotEventReconnect.Backoff.Length - 1)];
    }
}
