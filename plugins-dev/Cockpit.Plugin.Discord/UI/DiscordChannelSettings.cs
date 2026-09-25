using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord.UI;

// Discord-specific settings layered on the shared AC-1023 storage (`AssistantChannelStorage`): the bot token and
// which Discord text channel to relay into. Read fresh from `IPluginStorage` on every access, so a settings save
// takes effect without a restart. The UI part's own copy of the backend part's identically-named class under
// Settings/ (AC-1394) — both read the same storage slice through IPluginStorage, but a linked copy under the same
// namespace would be ambiguous to Cockpit.Plugin.Discord.Tests, which sees both assemblies' internals.
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
