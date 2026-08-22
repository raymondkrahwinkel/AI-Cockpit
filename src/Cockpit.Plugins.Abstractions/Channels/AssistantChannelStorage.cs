namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>
/// Where a channel plugin keeps its settings (AC-1023): who may talk and how much is relayed as ordinary values,
/// the bot token through <see cref="IPluginStorage.SetSecret"/> so it is never written in the clear.
/// </summary>
public static class AssistantChannelStorage
{
    private const string _AudienceKey = "assistantChannel.audience";
    private const string _UserIdsKey = "assistantChannel.userIds";
    private const string _VerbosityKey = "assistantChannel.verbosity";
    private const string _BotTokenKey = "assistantChannel.botToken";

    /// <summary>
    /// Writes the level the operator settled on and how much this channel relays.
    /// </summary>
    public static void Save(IPluginStorage storage, AssistantChannelAccess access, AssistantChannelVerbosity verbosity)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(access);

        storage.Set(_AudienceKey, access.Audience.ToString());
        storage.Set(_UserIdsKey, access.UserIds.ToArray());
        storage.Set(_VerbosityKey, verbosity.ToString());
    }

    /// <summary>
    /// Reads back what <see cref="Save"/> stored, or null when nothing is configured yet — a plugin with no channel
    /// to open, not a default to invent.
    /// </summary>
    public static (AssistantChannelAccess Access, AssistantChannelVerbosity Verbosity)? Load(IPluginStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        if (!Enum.TryParse<AssistantChannelAudience>(storage.Get<string>(_AudienceKey), out var audience))
        {
            return null;
        }

        var userIds = storage.Get<string[]>(_UserIdsKey) ?? [];

        // A named level with nothing named would let everyone past `IsAllowed`; read it as not configured.
        if (audience != AssistantChannelAudience.Everyone && userIds.Length == 0)
        {
            return null;
        }

        if (!Enum.TryParse<AssistantChannelVerbosity>(storage.Get<string>(_VerbosityKey), out var verbosity))
        {
            verbosity = AssistantChannelVerbosity.FinalAnswerOnly;
        }

        return (AssistantChannelAccess.Restore(audience, userIds), verbosity);
    }

    /// <summary>
    /// Stores the bot token as a credential, never as a plain setting.
    /// </summary>
    public static void SaveBotToken(IPluginStorage storage, string token)
    {
        ArgumentNullException.ThrowIfNull(storage);
        storage.SetSecret(_BotTokenKey, token);
    }

    /// <summary>
    /// Reads the bot token back, or null when none has been stored.
    /// </summary>
    public static string? LoadBotToken(IPluginStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage.GetSecret(_BotTokenKey);
    }
}
