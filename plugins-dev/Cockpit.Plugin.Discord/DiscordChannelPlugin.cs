using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            "window shows. Connects with a bot token over Discord.NET, in direct messages or one channel; consent " +
            "prompts relay as Approve/Deny buttons with a \"type JA/NEE\" text fallback. Adds a flow step that " +
            "DMs you.");

    private ICockpitHost? _host;
    private DiscordChannelSettings? _settings;
    private DiscordGatewayConnection? _connection;
    private ILogger? _logger;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        _host = host;
        _settings = new DiscordChannelSettings(host.Storage);

        host.AddSettings(() => new DiscordChannelSettingsControl(host, _settings), "Assistant Plugins");
        host.OnSettingsSaved(_Reconnect);

        // Resolved from the host's container, like the Depot plugin's (AC-499): a refused sender belongs in the log.
        _logger = host.Services.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.Plugin.Discord");

        var settings = _settings;
        host.AddWorkflowStep(new DiscordSendDmStep(() => settings.Access?.Access, _SendDirectMessageAsync));

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

        // Nothing configured yet — no access to open with (AssistantChannelStorage.Load's own "null is not a default
        // to invent" rule), or the operator has not entered a token yet.
        if (settings.Access is not { } configured || string.IsNullOrWhiteSpace(settings.BotToken))
        {
            return;
        }

        // AC-1360: direct messages are for the one allowed account only. The settings view refuses anything else,
        // so this is a stored value from before that rule — said out loud, and nothing opens.
        if (settings.ChannelId == 0 && configured.Access.Audience != AssistantChannelAudience.SingleUser)
        {
            host.ShowToast("Discord: direct messages are for a single account only — enter a channel id in the settings.", PluginToastSeverity.Error);
            return;
        }

        // AC-1074: same gap as Slack's — the shape check runs on save, so an id stored before it survives every load
        // and then matches nothing Discord can ever send. Said out loud here; the relay still opens.
        if (DiscordUserId.ValidateAll(configured.Access.UserIds) is { } accessError)
        {
            host.ShowToast($"Discord: no message will reach the assistant — {accessError}", PluginToastSeverity.Error);
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
            error => host.ShowToast(error, PluginToastSeverity.Error),
            refusal => _logger?.LogInformation("{Refusal}", refusal));
    }

    private Task _SendDirectMessageAsync(string userId, IReadOnlyList<string> parts, CancellationToken cancellationToken) =>
        _connection is { } connection
            ? connection.SendDirectMessageAsync(userId, parts, cancellationToken)
            : throw new InvalidOperationException("Nothing was sent: Discord is not connected. Check the bot token in the Discord plugin's settings.");

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
