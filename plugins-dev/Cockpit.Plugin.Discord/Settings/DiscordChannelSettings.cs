using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord.Settings;

// Discord-specific settings layered on the shared AC-1023 storage (`AssistantChannelStorage`): the
// bot token and which Discord text channel to relay into. Read fresh from `IPluginStorage` on every
// access, so a settings save takes effect without a restart.
internal sealed class DiscordChannelSettings(IPluginStorage storage)
{
    public const string ChannelIdKey = "discordChannelId";

    public string? BotToken
    {
        get => AssistantChannelStorage.LoadBotToken(storage);
        set => AssistantChannelStorage.SaveBotToken(storage, value ?? string.Empty);
    }

    public (AssistantChannelAccess Access, AssistantChannelVerbosity Verbosity)? Access => AssistantChannelStorage.Load(storage);

    public void SaveAccess(AssistantChannelAccess access, AssistantChannelVerbosity verbosity) =>
        AssistantChannelStorage.Save(storage, access, verbosity);
}
