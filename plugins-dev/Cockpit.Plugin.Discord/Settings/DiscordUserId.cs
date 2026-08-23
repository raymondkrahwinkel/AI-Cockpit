namespace Cockpit.Plugin.Discord.Settings;

// AC-1048: `AssistantChannelAccess` compares ordinal against whatever the platform hands over as a sender, which
// for Discord is a numeric snowflake — never a display name or a `username#0000` tag. Discord hides that id
// behind Developer Mode, so a rejected value points at where to find it, not just that it is wrong.
// Platform-specific on purpose: the shared abstraction (AssistantChannelAccess) stays agnostic to what a
// "user id" looks like.
internal static class DiscordUserId
{
    public const string HowToFind =
        "Find it in Discord: enable Developer Mode (Settings → Advanced), then right-click the account and choose \"Copy User ID\".";

    public static bool IsValid(string userId) =>
        userId.Length is >= 17 and <= 20 && userId.All(char.IsAsciiDigit);

    public static string? Validate(string userId) =>
        IsValid(userId)
            ? null
            : $"\"{userId}\" is not a Discord user id — it should be a string of 17-20 digits (a snowflake). {HowToFind}";
}
