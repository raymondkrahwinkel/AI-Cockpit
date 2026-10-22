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
    private readonly DiscordHealthState _health;
    private readonly ulong _channelId;
    private readonly string? _directMessageUserId;

    // AC-1544: the one account another plugin's message may go to as a DM, in either mode; null when several are allowed.
    private readonly string? _allowedUserId;

    // One DM channel per account, resolved once: every relayed row, edit and reaction would otherwise cost two REST calls.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IMessageChannel> _directMessageChannels = new(StringComparer.Ordinal);
    private bool _disposed;

    public DiscordGatewayConnection(
        IAssistantChannelGateway gateway,
        string botToken,
        ulong channelId,
        AssistantChannelAccess access,
        Func<AssistantChannelVerbosity> verbosity,
        DiscordHealthState health,
        Action<string> reportError,
        Action<string> logRefusal)
    {
        _channelId = channelId;
        _allowedUserId = access is { Audience: AssistantChannelAudience.SingleUser, UserIds.Count: 1 } ? access.UserIds.First() : null;
        _directMessageUserId = channelId == 0 ? _allowedUserId : null;
        _health = health;

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages | GatewayIntents.MessageContent,
        });

        var sink = new DiscordChannelSink(_ResolveChannelAsync);
        _files = new DiscordFileFetcher();
        _bridge = new DiscordChannelBridge(gateway, sink, _files, access, verbosity, reportError, logRefusal);

        _client.MessageReceived += _OnMessageReceived;
        _client.ButtonExecuted += _OnButtonExecutedAsync;
        _client.Ready += _OnReady;
        _client.Disconnected += _OnDisconnected;

        _ = _ConnectAsync(botToken, reportError);
    }

    private async Task _ConnectAsync(string botToken, Action<string> reportError)
    {
        try
        {
            await _client.LoginAsync(TokenType.Bot, botToken).ConfigureAwait(false);
            await _client.StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _health.Disconnected();
            // AC-1024 criterion 5: report once, with no reconnect loop to run away.
            try
            {
                reportError($"Discord: could not connect — {exception.Message}");
            }
            catch (Exception)
            {
                // The background connector has no caller left to observe a reporting failure.
            }
        }
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

    // The workflow step's way out (AC-1360): the parts go in order to the one account's direct messages, by the same
    // send as another plugin's message (AC-1544), so a DM has one implementation.
    public async Task SendDirectMessageAsync(string userId, IReadOnlyList<string> parts, CancellationToken cancellationToken)
    {
        if (userId != _allowedUserId)
        {
            throw new InvalidOperationException($"Nothing was sent: {userId} is not the one account the Discord settings allow.");
        }

        foreach (var part in parts)
        {
            var result = await SendOperatorMessageAsync(DiscordChannelPlugin.DirectMessageTarget, new OperatorMessage { Text = part }, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                throw new InvalidOperationException($"Nothing more was sent: {result.Error}");
            }
        }
    }

    // AC-1544: another plugin's message, to the one allowed account's DMs or to the configured channel, posted by the
    // bridge so its option buttons are the ones a row's question gets.
    public async Task<OperatorMessageResult> SendOperatorMessageAsync(string targetId, OperatorMessage message, CancellationToken cancellationToken)
    {
        if (targetId == DiscordChannelPlugin.DirectMessageTarget && _allowedUserId is { } userId)
        {
            var result = await _bridge.SendOperatorMessageAsync(
                new DiscordChannelSink(async () => await _DirectMessageChannelAsync(userId, cancellationToken).ConfigureAwait(false)),
                message,
                cancellationToken).ConfigureAwait(false);
            if (result.Ok)
            {
                _health.DirectMessageDelivered();
            }
            else
            {
                _health.DirectMessageFailed();
            }

            return result;
        }

        if (targetId == DiscordChannelPlugin.ChannelTarget && _channelId != 0)
        {
            return await _bridge.SendOperatorMessageAsync(
                new DiscordChannelSink(() => Task.FromResult(_client.GetChannel(_channelId) as IMessageChannel)),
                message,
                cancellationToken).ConfigureAwait(false);
        }

        return OperatorMessageResult.Failed($"Discord cannot deliver to '{targetId}' with its current settings.");
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

        _health.MessageReceived();
        return _bridge.HandleInboundMessageAsync(
            message.Author.Id.ToString(), message.Content, message.Id, _InboundFiles(message));
    }

    private Task _OnReady()
    {
        _health.Connected(_client.CurrentUser?.Username);
        return Task.CompletedTask;
    }

    private Task _OnDisconnected(Exception _)
    {
        _health.Disconnected();
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
        _client.Ready -= _OnReady;
        _client.Disconnected -= _OnDisconnected;
        _bridge.Dispose();
        _files.Dispose();
        _health.Disconnected();
        _ = _StopAsync();
    }

    private async Task _StopAsync()
    {
        try
        {
            await _client.StopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal is best effort, but the background stop is always observed.
        }
        finally
        {
            try
            {
                _client.Dispose();
            }
            catch (Exception)
            {
                // The background stop has already made the connection unavailable.
            }
        }
    }
}
