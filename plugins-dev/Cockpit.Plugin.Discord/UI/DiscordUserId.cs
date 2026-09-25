namespace Cockpit.Plugin.Discord.UI;

// AC-1048: platform-specific shape check for what AssistantChannelAccess compares ordinal against. The UI part's
// own copy of the backend part's identically-named class under Settings/ — see DiscordChannelSettings.cs's own
// remarks for why this is a separate copy rather than a linked one.
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

    // AC-1074: the first error in a stored access list, or null when every id has the right shape.
    public static string? ValidateAll(IEnumerable<string> userIds) =>
        userIds.Select(Validate).FirstOrDefault(error => error is not null);
}
