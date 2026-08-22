using global::Discord;
using global::Discord.WebSocket;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord;

// Owns the Discord.NET socket connection for one open assistant channel (AC-1024), wiring inbound messages and
// button clicks into a `DiscordChannelBridge`. A bad token fails at `LoginAsync`, before
// `StartAsync` runs, so it is reported once with no reconnect loop; Discord.NET itself then owns reconnects.
internal sealed class DiscordGatewayConnection : IDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly DiscordChannelBridge _bridge;
    private readonly ulong _channelId;
    private bool _disposed;

    public DiscordGatewayConnection(
        IAssistantChannelGateway gateway,
        string botToken,
        ulong channelId,
        AssistantChannelAccess access,
        Func<AssistantChannelVerbosity> verbosity,
        Action<string> reportConnectionError)
    {
        _channelId = channelId;

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
        });

        var sink = new DiscordChannelSink(_ResolveChannelAsync);
        _bridge = new DiscordChannelBridge(gateway, sink, access, verbosity);

        _client.MessageReceived += _OnMessageReceived;
        _client.ButtonExecuted += _OnButtonExecutedAsync;

        _ = _ConnectAsync(botToken, reportConnectionError);
    }

    private async Task _ConnectAsync(string botToken, Action<string> reportConnectionError)
    {
        try
        {
            await _client.LoginAsync(TokenType.Bot, botToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // AC-1024 criterion 5: reported once, and StartAsync below never runs — no reconnect loop to run away.
            reportConnectionError($"Discord: could not connect — {exception.Message}");
            return;
        }

        await _client.StartAsync().ConfigureAwait(false);
    }

    private Task<ITextChannel?> _ResolveChannelAsync() => Task.FromResult(_client.GetChannel(_channelId) as ITextChannel);

    private Task _OnMessageReceived(SocketMessage message)
    {
        if (message.Author.IsBot || message.Channel.Id != _channelId)
        {
            return Task.CompletedTask;
        }

        _ = _bridge.HandleInboundMessageAsync(message.Author.Id.ToString(), message.Content, message.Id);
        return Task.CompletedTask;
    }

    private async Task _OnButtonExecutedAsync(SocketMessageComponent component)
    {
        // Discord's 3-second interaction-ack deadline (AC-1024): defer immediately, decide and edit afterwards.
        await component.DeferAsync().ConfigureAwait(false);
        await _bridge.HandleButtonAsync(component.Data.CustomId, component.User.Id.ToString()).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.MessageReceived -= _OnMessageReceived;
        _client.ButtonExecuted -= _OnButtonExecutedAsync;
        _bridge.Dispose();
        _ = _client.StopAsync();
        _client.Dispose();
    }
}
