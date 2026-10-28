using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Plugin.Discord.Contracts;
using Cockpit.Plugin.Discord.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Discord;

// Discord plugin entry point (AC-1024, EPIC AC-669): a Discord.NET bot as a second door onto the assistant's own
// conversation, through the AC-1023 AssistantChannelContribution seam (identity/consent filtering stay
// host-side there). Supplies what is Discord-specific: the socket, Components-API consent buttons, relay.
public sealed class DiscordChannelPlugin : ICockpitPlugin, IOperatorMessageChannel
{
    // AC-1544: where another plugin's message can go — never an arbitrary id, only what the access settings allow.
    public const string DirectMessageTarget = "dm";

    public PluginMetadata Metadata { get; } = new(
        Id: "discord",
        DisplayName: "Discord",
        Author: "Cockpit",
        Description: "Talk to your assistant from Discord — a second door onto the same conversation the chat " +
            "window shows. Connects with a bot token over Discord.NET in direct messages; consent " +
            "prompts relay as Approve/Deny buttons with a \"type JA/NEE\" text fallback. Adds a flow step that " +
            "DMs you.");

    private readonly List<IDisposable> _handlers = [];
    private readonly DiscordHealthState _health = new(TimeProvider.System);

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
        host.AddHealthSection(_health);

        if (AssistantChannelStorage.MigrateToSingleUser(host.Storage, DiscordChannelSettings.ChannelIdKey) is { } notice)
        {
            host.ShowToast($"Discord: {notice}", PluginToastSeverity.Information);
        }

        // AC-1394: the settings view itself, and the AddSettings/OnSettingsSaved registrations that put it in
        // front of the operator, moved to DiscordUi.InitializeUi — this reconnect is what the UI part's own
        // OnSettingsSaved callback now asks for over the plugin's channel instead of calling directly.
        _handlers.Add(host.Channel.Handle(DiscordChannel.SettingsSaved, (_, _) =>
        {
            _Reconnect();
            return Task.FromResult(JsonSerializer.SerializeToElement<object?>(null, DiscordChannel.Json));
        }));

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
        _health.Disconnected();
        _connection?.Dispose();
        _connection = null;
        _health.Disconnected("Not configured");

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

        _health.Disconnected("Connecting");

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
            Messages = this,
        };

        // Null on a host with no assistant (a headless/test host) — nothing to connect to.
        if (host.OpenAssistantChannel(contribution) is not { } gateway)
        {
            _health.Disconnected("Assistant channel unavailable");
            return;
        }

        _connection = new DiscordGatewayConnection(
            gateway,
            settings.BotToken,
            configured.Access,
            () => settings.Access?.Verbosity ?? AssistantChannelVerbosity.FinalAnswerOnly,
            _health,
            error => host.ShowToast(error, PluginToastSeverity.Error),
            refusal => _logger?.LogInformation("{Refusal}", refusal));
    }

    public IReadOnlyList<OperatorMessageTarget> Targets
    {
        get
        {
            List<OperatorMessageTarget> targets = [];
            if (_settings?.Access?.Access is { Audience: AssistantChannelAudience.SingleUser, UserIds.Count: 1 })
            {
                targets.Add(new OperatorMessageTarget(DirectMessageTarget, "Direct message to you"));
            }

            return targets;
        }
    }

    public Task<OperatorMessageResult> SendAsync(string targetId, OperatorMessage message, CancellationToken cancellationToken = default) =>
        _connection is { } connection
            ? connection.SendOperatorMessageAsync(targetId, message, cancellationToken)
            : Task.FromResult(OperatorMessageResult.Failed("Discord is not connected. Check the bot token in the Discord plugin's settings."));

    private Task _SendDirectMessageAsync(string userId, IReadOnlyList<string> parts, CancellationToken cancellationToken) =>
        _connection is { } connection
            ? connection.SendDirectMessageAsync(userId, parts, cancellationToken)
            : throw new InvalidOperationException("Nothing was sent: Discord is not connected. Check the bot token in the Discord plugin's settings.");

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();

        _connection?.Dispose();
        _connection = null;
    }
}
