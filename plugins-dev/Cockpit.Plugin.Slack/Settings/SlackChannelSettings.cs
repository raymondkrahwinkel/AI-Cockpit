using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Slack.Settings;

// Slack-specific settings layered on the shared AC-1023 storage (`AssistantChannelStorage`): the bot token
// (shared key), the app-level token Socket Mode needs (this plugin's own secret — Slack Socket Mode needs both,
// unlike Discord's single bot token), and which Slack channel to relay into. Read fresh from `IPluginStorage`
// on every access, so a settings save takes effect without a restart.
internal sealed class SlackChannelSettings(IPluginStorage storage)
{
    private const string _AppLevelTokenKey = "slack.appLevelToken";
    private const string _ChannelIdKey = "slackChannelId";

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

    public string? ChannelId
    {
        get => storage.Get<string>(_ChannelIdKey);
        set => storage.Set(_ChannelIdKey, value ?? string.Empty);
    }

    public (AssistantChannelAccess Access, AssistantChannelVerbosity Verbosity)? Access => AssistantChannelStorage.Load(storage);

    public void SaveAccess(AssistantChannelAccess access, AssistantChannelVerbosity verbosity) =>
        AssistantChannelStorage.Save(storage, access, verbosity);
}
