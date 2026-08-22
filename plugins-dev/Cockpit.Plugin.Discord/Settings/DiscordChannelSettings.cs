using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord.Settings;

/// <summary>
/// Discord-specific settings layered on the shared AC-1023 storage (<see cref="AssistantChannelStorage"/>): the
/// bot token and which Discord text channel to relay into. Read fresh from <see cref="IPluginStorage"/> on every
/// access, so a settings save takes effect without a restart.
/// </summary>
internal sealed class DiscordChannelSettings(IPluginStorage storage)
{
    public string? BotToken
    {
        get => AssistantChannelStorage.LoadBotToken(storage);
        set => AssistantChannelStorage.SaveBotToken(storage, value ?? string.Empty);
    }

    public ulong ChannelId
    {
        get => ulong.TryParse(storage.Get<string>("discordChannelId"), out var id) ? id : 0;
        set => storage.Set("discordChannelId", value.ToString());
    }

    public (AssistantChannelAccess Access, AssistantChannelVerbosity Verbosity)? Access => AssistantChannelStorage.Load(storage);

    public void SaveAccess(AssistantChannelAccess access, AssistantChannelVerbosity verbosity) =>
        AssistantChannelStorage.Save(storage, access, verbosity);
}
