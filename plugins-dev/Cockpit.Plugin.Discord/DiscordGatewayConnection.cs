using global::Discord;
using global::Discord.WebSocket;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord;

// Owns the Discord.NET socket connection for one open assistant channel (AC-1024), wiring inbound messages and
// button clicks into a `DiscordChannelBridge`. A bad token fails at `LoginAsync`, before `StartAsync` runs, so it is
// reported once with no reconnect loop. A channel id of 0 means direct messages with the one allowed account (AC-1360).
internal sealed class DiscordGatewayConnection : IDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly DiscordChannelBridge _bridge;
    private readonly DiscordFileFetcher _files;
    private readonly ulong _channelId;
    private readonly string? _directMessageUserId;

    // One DM channel per account, resolved once: every relayed row, edit and reaction would otherwise cost two REST calls.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IMessageChannel> _directMessageChannels = new(StringComparer.Ordinal);
    private bool _disposed;

    public DiscordGatewayConnection(
        IAssistantChannelGateway gateway,
        string botToken,
        ulong channelId,
        AssistantChannelAccess access,
        Func<AssistantChannelVerbosity> verbosity,
        Action<string> reportError,
        Action<string> logRefusal)
    {
        _channelId = channelId;
        _directMessageUserId = channelId == 0 && access is { Audience: AssistantChannelAudience.SingleUser, UserIds.Count: 1 }
            ? access.UserIds.First()
            : null;

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages | GatewayIntents.MessageContent,
        });

        var sink = new DiscordChannelSink(_ResolveChannelAsync);
        _files = new DiscordFileFetcher();
        _bridge = new DiscordChannelBridge(gateway, sink, _files, access, verbosity, reportError, logRefusal);

        _client.MessageReceived += _OnMessageReceived;
        _client.ButtonExecuted += _OnButtonExecutedAsync;

        _ = _ConnectAsync(botToken, reportError);
    }

    private async Task _ConnectAsync(string botToken, Action<string> reportError)
    {
        try
        {
            await _client.LoginAsync(TokenType.Bot, botToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // AC-1024 criterion 5: reported once, and StartAsync below never runs — no reconnect loop to run away.
            reportError($"Discord: could not connect — {exception.Message}");
            return;
        }

        await _client.StartAsync().ConfigureAwait(false);
    }

    private async Task<IMessageChannel?> _ResolveChannelAsync()
    {
        if (_directMessageUserId is { } userId)
        {
            return await _DirectMessageChannelAsync(userId).ConfigureAwait(false);
        }

        return _channelId == 0 ? null : _client.GetChannel(_channelId) as ITextChannel;
    }

    private async Task<IMessageChannel> _DirectMessageChannelAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (_directMessageChannels.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        if (!ulong.TryParse(userId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            throw new InvalidOperationException($"'{userId}' is not a Discord user id. Correct it in the Discord plugin's settings.");
        }

        var options = new RequestOptions { CancelToken = cancellationToken };
        var user = await _client.Rest.GetUserAsync(id, options).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Discord does not know the account {userId}.");
        var channel = await user.CreateDMChannelAsync(options).ConfigureAwait(false);
        return _directMessageChannels.GetOrAdd(userId, channel);
    }

    // The workflow step's way out (AC-1360): the parts go in order to the one account's direct messages.
    public async Task SendDirectMessageAsync(string userId, IReadOnlyList<string> parts, CancellationToken cancellationToken)
    {
        var channel = await _DirectMessageChannelAsync(userId, cancellationToken).ConfigureAwait(false);
        foreach (var part in parts)
        {
            await channel.SendMessageAsync(part, options: new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false);
        }
    }

    private Task _OnMessageReceived(SocketMessage message)
    {
        // A join or a pin arrives as a SocketSystemMessage — Discord's counterpart of Slack's system subtypes
        // (AC-1046). A user message keeps its text whether or not a file hangs off it.
        if (message is not SocketUserMessage
            || !DiscordInboundFilter.Accepts(message.Author.IsBot, message.Channel is IDMChannel, message.Channel.Id, _channelId))
        {
            return Task.CompletedTask;
        }

        _ = _bridge.HandleInboundMessageAsync(
            message.Author.Id.ToString(), message.Content, message.Id, _InboundFiles(message));
        return Task.CompletedTask;
    }

    private static IReadOnlyList<DiscordInboundFile> _InboundFiles(SocketMessage message) =>
        message.Attachments
            .Select(attachment => new DiscordInboundFile(
                attachment.Filename, attachment.ContentType, attachment.Size, attachment.Url))
            .ToList();

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
        _files.Dispose();
        _ = _client.StopAsync();
        _client.Dispose();
    }
}
