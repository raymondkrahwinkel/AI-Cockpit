using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Slack.Settings;

// Slack-specific settings on top of the shared AC-1023 storage: the bot token (shared key), the app-level
// token Socket Mode needs (this plugin's own secret — Slack needs both, unlike Discord's single bot token),
// and which Slack channel to relay into. Read fresh from `IPluginStorage`, so a save takes effect without a restart.
internal sealed class SlackChannelSettings(IPluginStorage storage)
{
    private const string _AppLevelTokenKey = "slack.appLevelToken";
    public const string ChannelIdKey = "slackChannelId";
    public const string ReauthorizationNotice =
        "Reinstall and authorize the Slack app with the new DM scopes and event, or no messages will arrive.";

    public string? BotToken
    {
        get => AssistantChannelStorage.LoadBotToken(storage);
        set => AssistantChannelStorage.SaveBotToken(storage, value ?? string.Empty);
    }

    public string? AppLevelToken
    {
        get => storage.GetSecret(_AppLevelTokenKey);
        set => storage.SetSecret(_AppLevelTokenKey, value ?? string.Empty);
    }

    public (AssistantChannelAccess Access, AssistantChannelVerbosity Verbosity)? Access => AssistantChannelStorage.Load(storage);

    public AssistantChannelVerbosity Verbosity => AssistantChannelStorage.LoadVerbosity(storage);

    public string? MigrationNotice => AssistantChannelStorage.LoadMigrationNotice(storage) is { } notice
        ? $"{notice} {ReauthorizationNotice}"
        : null;

    public void SaveAccess(AssistantChannelAccess access, AssistantChannelVerbosity verbosity) =>
        AssistantChannelStorage.Save(storage, access, verbosity);
}
