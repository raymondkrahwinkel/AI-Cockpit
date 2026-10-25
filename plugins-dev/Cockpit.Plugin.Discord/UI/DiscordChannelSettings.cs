using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord.UI;

// Discord-specific settings layered on the shared channel storage. Read fresh from IPluginStorage on every access,
// so a settings save takes effect without a restart. The UI part's own copy of the backend class under
// Settings/ (AC-1394) — both read the same storage slice through IPluginStorage, but a linked copy under the same
// namespace would be ambiguous to Cockpit.Plugin.Discord.Tests, which sees both assemblies' internals.
internal sealed class DiscordChannelSettings(IPluginStorage storage)
{
    public string? BotToken
    {
        get => AssistantChannelStorage.LoadBotToken(storage);
        set => AssistantChannelStorage.SaveBotToken(storage, value ?? string.Empty);
    }

    public (AssistantChannelAccess Access, AssistantChannelVerbosity Verbosity)? Access => AssistantChannelStorage.Load(storage);

    public AssistantChannelVerbosity Verbosity => AssistantChannelStorage.LoadVerbosity(storage);

    public string? MigrationNotice => AssistantChannelStorage.LoadMigrationNotice(storage);

    public void SaveAccess(AssistantChannelAccess access, AssistantChannelVerbosity verbosity) =>
        AssistantChannelStorage.Save(storage, access, verbosity);
}
