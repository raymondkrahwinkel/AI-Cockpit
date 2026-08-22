using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Discord.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Discord;

// Discord plugin entry point (AC-1024, EPIC AC-669): a Discord.NET bot as a second door onto the assistant's own
// conversation, through the AC-1023 AssistantChannelContribution seam (identity/consent filtering stay
// host-side there). Supplies what is Discord-specific: the socket, Components-API consent buttons, relay.
public sealed class DiscordChannelPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "discord",
        DisplayName: "Discord",
        Author: "Cockpit",
        Description: "Talk to your assistant from Discord — a second door onto the same conversation the chat " +
            "window shows. Connects with a bot token over Discord.NET; consent prompts relay as Approve/Deny " +
            "buttons with a \"type JA/NEE\" text fallback.");

    private ICockpitHost? _host;
    private DiscordChannelSettings? _settings;
    private DiscordGatewayConnection? _connection;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        _host = host;
        _settings = new DiscordChannelSettings(host.Storage);

        host.AddSettings(() => new DiscordChannelSettingsControl(host, _settings));
        host.OnSettingsSaved(_Reconnect);

        _Reconnect();
    }

    // Rebuilds the Discord connection from whatever is currently in storage — at startup and after every
    // settings save, so a token/channel/access change takes effect without a restart (the access level is baked
    // into the AssistantChannelContribution, so a narrower "just swap the token" path would still need this).
    private void _Reconnect()
    {
        _connection?.Dispose();
        _connection = null;

        if (_host is not { } host || _settings is not { } settings)
        {
            return;
        }

        // Nothing configured yet — no channel to open (AssistantChannelStorage.Load's own "null is not a default
        // to invent" rule), or the operator has not entered a token/channel yet.
        if (settings.Access is not { } configured || string.IsNullOrWhiteSpace(settings.BotToken) || settings.ChannelId == 0)
        {
            return;
        }

        var contribution = new AssistantChannelContribution
        {
            Id = "discord",
            Name = "Discord",
            Access = configured.Access,
            Verbosity = configured.Verbosity,
        };

        // Null on a host with no assistant (a headless/test host) — nothing to connect to.
        if (host.OpenAssistantChannel(contribution) is not { } gateway)
        {
            return;
        }

        _connection = new DiscordGatewayConnection(
            gateway,
            settings.BotToken,
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
