namespace Cockpit.Plugin.Discord.Settings;

// AC-1048: platform-specific shape check for what AssistantChannelAccess compares ordinal against.
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
