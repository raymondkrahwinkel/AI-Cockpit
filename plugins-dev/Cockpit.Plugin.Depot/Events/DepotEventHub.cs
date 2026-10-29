using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cockpit.Plugin.Depot.Events;

internal sealed record DepotEvent(string Topic, string Kind, string? Address, long Version);

internal interface IDepotEventHub : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);

    Task SubscribeAsync(string topic, CancellationToken cancellationToken);
}

internal delegate IDepotEventHub DepotEventHubFactory(
    DepotConnectionRegistration connection,
    string userId,
    Action<DepotEvent> onEvent,
    Func<Task> onReconnected,
    Func<Exception?, Task> onClosed);

internal static class DepotEventReconnect
{
    internal static readonly TimeSpan[] Backoff = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
}

internal sealed class SignalRDepotEventHub : IDepotEventHub
{
    private readonly HubConnection _connection;

    public SignalRDepotEventHub(
        DepotConnectionRegistration connection,
        string userId,
        Func<HttpMessageHandler> handler,
        Action<DepotEvent> onEvent,
        Func<Task> onReconnected,
        Func<Exception?, Task> onClosed)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(
                $"{connection.Url.TrimEnd('/')}/hubs/cockpit?expectedUser={Uri.EscapeDataString(userId)}",
                options => DepotSignalR.Configure(options, handler))
            .WithAutomaticReconnect(DepotEventReconnect.Backoff)
            .Build();
        _connection.On<JsonElement>("event", payload =>
        {
            if (payload.Deserialize<DepotEvent>(DepotChannel.Json) is { } cockpitEvent)
            {
                onEvent(cockpitEvent);
            }
        });
        _connection.Reconnected += _ => onReconnected();
        _connection.Closed += onClosed;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _connection.StartAsync(cancellationToken);

    public Task SubscribeAsync(string topic, CancellationToken cancellationToken) =>
        _connection.InvokeAsync("Subscribe", topic, cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
