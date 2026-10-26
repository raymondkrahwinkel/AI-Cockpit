namespace Cockpit.Plugin.Discord;

// The socket subscribes only to direct messages; this second lock rejects channel traffic and other bots too.
internal static class DiscordInboundFilter
{
    public static bool Accepts(bool fromBot, bool isDirectMessage) => !fromBot && isDirectMessage;
}
