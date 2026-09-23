namespace Cockpit.Plugin.Discord;

// Where a message may come from before the bridge even looks at who sent it (AC-1360, AC-625 §5). Without a
// configured channel only a direct message counts; with one, only that channel does. Another bot never counts.
internal static class DiscordInboundFilter
{
    public static bool Accepts(bool fromBot, bool isDirectMessage, ulong channelId, ulong optInChannelId) =>
        !fromBot && (optInChannelId == 0 ? isDirectMessage : !isDirectMessage && channelId == optInChannelId);
}
