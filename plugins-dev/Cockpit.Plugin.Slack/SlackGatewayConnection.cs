using SlackNet;
using SlackNet.Blocks;
using SlackNet.Events;
using SlackNet.Interaction;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Slack;

// Owns the SlackNet Socket Mode connection for one open assistant channel (AC-1025), wiring inbound messages
// and button clicks into a `SlackChannelBridge`. A bad app-level token fails inside `Connect()` before any
// socket opens, so it is reported once with no reconnect loop — the same shape as Discord's bad-token failure.
internal sealed class SlackGatewayConnection : IDisposable, IEventHandler<MessageEvent>, IBlockActionHandler<ButtonAction>
{
    private readonly ISlackSocketModeClient _client;
    private readonly SlackChannelBridge _bridge;
    private readonly string _channelId;
    private bool _disposed;

    public SlackGatewayConnection(
        IAssistantChannelGateway gateway,
        string botToken,
        string appLevelToken,
        string channelId,
        AssistantChannelAccess access,
        Func<AssistantChannelVerbosity> verbosity,
        Action<string> reportConnectionError)
    {
        _channelId = channelId;

        var builder = new SlackServiceBuilder()
            .UseApiToken(botToken)
            .UseAppLevelToken(appLevelToken)
            .RegisterEventHandler<MessageEvent>(this)
            .RegisterBlockActionHandler<ButtonAction>(this);

        var sink = new SlackChannelSink(builder.GetApiClient(), channelId);
        _bridge = new SlackChannelBridge(gateway, sink, access, verbosity);
        _client = builder.GetSocketModeClient();

        _ = _ConnectAsync(reportConnectionError);
    }

    private async Task _ConnectAsync(Action<string> reportConnectionError)
    {
        try
        {
            await _client.Connect().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // AC-1025 criterion 5: reported once, no reconnect loop.
            reportConnectionError($"Slack: could not connect — {SlackConnectionErrorFormatter.Explain(exception)}");
        }
    }

    // Slack gives an ordinary user message a subtype too as soon as a file hangs off it (`file_share`), so only the
    // known bot/system subtypes are named here — anything else is somebody talking (AC-1046).
    private static readonly HashSet<string> _ignoredSubtypes = new(StringComparer.Ordinal)
    {
        "bot_message",
        "bot_add",
        "bot_remove",
        "message_changed",
        "message_deleted",
        "message_replied",
        "file_comment",
        "sh_room_created",
        "app_conversation_leave",
        "channel_join",
        "channel_leave",
        "channel_topic",
        "channel_purpose",
        "channel_name",
        "channel_archive",
        "channel_unarchive",
        "channel_convert_to_private",
        "channel_convert_to_public",
        "group_join",
        "group_leave",
        "group_topic",
        "group_purpose",
        "group_name",
        "group_archive",
        "group_unarchive",
        "pinned_item",
        "unpinned_item",
        "reminder_add",
        "ekm_access_denied",
        "huddle_thread",
        "tombstone",
    };

    // Whether an inbound event is a real message from a real person on this channel.
    internal static bool ShouldHandle(string? botId, string? subtype, string? channel, string expectedChannel) =>
        botId is null
        && channel == expectedChannel
        && (subtype is null || !_ignoredSubtypes.Contains(subtype));

    // A message arrived on the socket.
    public Task Handle(MessageEvent slackEvent)
    {
        if (!ShouldHandle(slackEvent.BotId, slackEvent.Subtype, slackEvent.Channel, _channelId))
        {
            return Task.CompletedTask;
        }

        return _bridge.HandleInboundMessageAsync(slackEvent.User, slackEvent.Text, slackEvent.Ts);
    }

    // An Approve/Deny button was clicked. SlackNet acknowledges the interaction itself once this task
    // completes — unlike Discord's Components API there is no separate defer step to call first.
    public Task Handle(ButtonAction action, BlockActionRequest request) =>
        _bridge.HandleButtonAsync(action.ActionId, request.User.Id);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bridge.Dispose();
        _client.Disconnect();
        _client.Dispose();
    }
}
