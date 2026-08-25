using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Slack.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Slack;

// Slack plugin entry point (AC-1025, EPIC AC-669): a SlackNet Socket Mode bot as a second door onto the
// assistant's own conversation, through the AC-1023 AssistantChannelContribution seam (identity/consent
// filtering stay host-side there). Supplies what is Slack-specific: the socket, Block Kit consent buttons, relay.
public sealed class SlackChannelPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "slack",
        DisplayName: "Slack",
        Author: "Cockpit",
        Description: "Talk to your assistant from Slack — a second door onto the same conversation the chat " +
            "window shows. Connects over Socket Mode with a bot token and an app-level token via SlackNet; " +
            "consent prompts relay as Approve/Deny Block Kit buttons with a \"type JA/NEE\" text fallback.");

    private ICockpitHost? _host;
    private SlackChannelSettings? _settings;
    private SlackGatewayConnection? _connection;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        _host = host;
        _settings = new SlackChannelSettings(host.Storage);

        host.AddSettings(() => new SlackChannelSettingsControl(host, _settings), "Assistant Plugins");
        host.OnSettingsSaved(_Reconnect);

        _Reconnect();
    }

    // Rebuilds the Slack connection from whatever is currently in storage — at startup and after every
    // settings save, so a token/channel/access change takes effect without a restart.
    private void _Reconnect()
    {
        _connection?.Dispose();
        _connection = null;

        if (_host is not { } host || _settings is not { } settings)
        {
            return;
        }

        // Nothing configured yet — no channel to open (AssistantChannelStorage.Load's own "null is not a default
        // to invent" rule), or the operator has not entered both tokens and a channel yet.
        if (settings.Access is not { } configured
            || string.IsNullOrWhiteSpace(settings.BotToken)
            || string.IsNullOrWhiteSpace(settings.AppLevelToken)
            || string.IsNullOrWhiteSpace(settings.ChannelId))
        {
            return;
        }

        // AC-1074: shape-checked on save since AC-1048, but a value stored before that check survives every load and
        // then matches nothing Slack can ever send. Said out loud here; the relay still opens, outbound is unharmed.
        if (SlackUserId.ValidateAll(configured.Access.UserIds) is { } accessError)
        {
            host.ShowToast($"Slack: no message will reach the assistant — {accessError}", PluginToastSeverity.Error);
        }

        var contribution = new AssistantChannelContribution
        {
            Id = "slack",
            Name = "Slack",
            Access = configured.Access,
            Verbosity = configured.Verbosity,
        };

        // Null on a host with no assistant (a headless/test host) — nothing to connect to.
        if (host.OpenAssistantChannel(contribution) is not { } gateway)
        {
            return;
        }

        _connection = new SlackGatewayConnection(
            gateway,
            settings.BotToken,
            settings.AppLevelToken,
            settings.ChannelId,
            configured.Access,
            () => settings.Access?.Verbosity ?? AssistantChannelVerbosity.FinalAnswerOnly,
            error => host.ShowToast(error, PluginToastSeverity.Error));
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
