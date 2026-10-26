using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cockpit.Plugin.Depot.Inbox;

// AC-1586: one live connection to a Depot's /hubs/inbox, which pushes counts and never content.
internal interface IDepotInboxHub : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
}

// Builds the hub for a connection and the account Depot named, with what to do on a push and when it closes.
internal delegate IDepotInboxHub DepotInboxHubFactory(
    DepotConnectionRegistration connection, string userId, Action<DepotInboxPush> onPush, Func<Exception?, Task> onClosed);

internal sealed record DepotInboxPush(int Unread, IReadOnlyDictionary<string, int> PerOrganization);

// The real hub: SignalR over the host's authorized handler, WebSockets included, so the bearer stays in the host.
// Depot closes it after at most five minutes; reconnecting is DepotInboxService's, through onClosed.
internal sealed class SignalRDepotInboxHub : IDepotInboxHub
{
    private readonly HubConnection _connection;

    public SignalRDepotInboxHub(
        DepotConnectionRegistration connection,
        string userId,
        Func<HttpMessageHandler> handler,
        Action<DepotInboxPush> onPush,
        Func<Exception?, Task> onClosed)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(
                $"{connection.Url.TrimEnd('/')}/hubs/inbox?expectedUser={Uri.EscapeDataString(userId)}",
                options => options.HttpMessageHandlerFactory = _ => handler())
            .Build();
        _connection.On<JsonElement>("inbox", payload =>
        {
            if (payload.Deserialize<DepotInboxPush>(DepotChannel.Json) is { } push)
            {
                onPush(push);
            }
        });
        _connection.Closed += onClosed;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _connection.StartAsync(cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
