using SlackNet;
using SlackNet.Blocks;
using SlackNet.Events;
using SlackNet.Interaction;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Slack;

// Owns the SlackNet Socket Mode connection for one open assistant channel (AC-1025), wiring inbound messages
// and button clicks into a `SlackChannelBridge`. A bad app-level token fails inside `Connect()` — Slack's
// apps.connections.open call — before any socket opens, so it is reported once with no reconnect loop (the
// same shape as Discord's bad-token LoginAsync failure, AC-1024).
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

    // A message arrived on the socket. Bot/system messages (edits, deletions, our own posts) carry a subtype
    // or a BotId and are ignored, the same restraint Discord's MessageReceived gives its own IsBot check.
    public Task Handle(MessageEvent slackEvent)
    {
        if (slackEvent.BotId is not null || slackEvent.Subtype is not null || slackEvent.Channel != _channelId)
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
