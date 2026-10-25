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
    private readonly SlackFileFetcher _files;
    private readonly Action<string> _reportRefusal;
    private SlackChannelBridge? _bridge;
    private string? _channelId;
    private bool _disposed;

    public SlackGatewayConnection(
        IAssistantChannelGateway gateway,
        string botToken,
        string appLevelToken,
        string ownerUserId,
        AssistantChannelAccess access,
        Func<AssistantChannelVerbosity> verbosity,
        Action<string> reportError,
        Action<string> reportRefusal)
    {
        var builder = new SlackServiceBuilder()
            .UseApiToken(botToken)
            .UseAppLevelToken(appLevelToken)
            .RegisterEventHandler<MessageEvent>(this)
            .RegisterBlockActionHandler<ButtonAction>(this);

        var api = builder.GetApiClient();
        _files = new SlackFileFetcher(botToken);
        _reportRefusal = reportRefusal;
        _client = builder.GetSocketModeClient();

        _ = _ConnectAsync(api, gateway, ownerUserId, access, verbosity, reportError);
    }

    private async Task _ConnectAsync(
        ISlackApiClient api,
        IAssistantChannelGateway gateway,
        string ownerUserId,
        AssistantChannelAccess access,
        Func<AssistantChannelVerbosity> verbosity,
        Action<string> reportError)
    {
        try
        {
            var channelId = await api.Conversations.Open([ownerUserId]).ConfigureAwait(false);
            if (_disposed)
            {
                return;
            }

            _channelId = channelId;
            _bridge = new SlackChannelBridge(
                gateway,
                new SlackChannelSink(api, channelId),
                _files,
                access,
                verbosity,
                reportError,
                channelId,
                _reportRefusal);
            await _client.Connect().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // AC-1025 criterion 5: reported once, no reconnect loop.
            reportError($"Slack: could not connect — {SlackConnectionErrorFormatter.Explain(exception)}");
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

    // Whether an inbound event is a real message from a real person in the owner's DM.
    internal static bool ShouldHandle(
        string? botId,
        string? subtype,
        string? channel,
        string? channelType,
        string expectedChannel) =>
        botId is null
        && (channel != expectedChannel || channelType != "im")
        && (subtype is null || !_ignoredSubtypes.Contains(subtype));

    // A message arrived on the socket.
    public Task Handle(MessageEvent slackEvent)
    {
        if (_channelId is not { } channelId
            || !ShouldHandle(slackEvent.BotId, slackEvent.Subtype, slackEvent.Channel, slackEvent.ChannelType, channelId))
        {
            if (slackEvent.BotId is null
                && (slackEvent.Subtype is null || !_ignoredSubtypes.Contains(slackEvent.Subtype)))
            {
                _reportRefusal(Refusal(slackEvent.User, slackEvent.Channel));
            }

            return Task.CompletedTask;
        }

        return _bridge is { } bridge
            ? bridge.HandleInboundMessageAsync(slackEvent.User, slackEvent.Text, slackEvent.Ts, _InboundFiles(slackEvent))
            : Task.CompletedTask;
    }

    internal static string Refusal(string? senderId, string? channelId) =>
        $"Slack ignored a message from sender {senderId ?? "<unknown>"} in channel {channelId ?? "<unknown>"}.";

    private static IReadOnlyList<SlackInboundFile> _InboundFiles(MessageEvent slackEvent) =>
        slackEvent.Files?
            .Select(file => new SlackInboundFile(
                file.Name,
                file.Mimetype,
                file.Size,
                // The download URL when Slack offers one; both need the bot token either way.
                string.IsNullOrEmpty(file.UrlPrivateDownload) ? file.UrlPrivate : file.UrlPrivateDownload))
            .ToList() ?? [];

    // An Approve/Deny button was clicked. SlackNet acknowledges the interaction itself once this task
    // completes — unlike Discord's Components API there is no separate defer step to call first.
    public Task Handle(ButtonAction action, BlockActionRequest request) =>
        _bridge?.HandleButtonAsync(action.ActionId, request.User.Id) ?? Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bridge?.Dispose();
        _files.Dispose();
        _client.Disconnect();
        _client.Dispose();
    }
}
