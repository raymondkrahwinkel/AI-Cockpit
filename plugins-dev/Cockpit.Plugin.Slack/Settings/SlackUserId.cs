namespace Cockpit.Plugin.Slack.Settings;

// AC-1048: platform-specific shape check for what AssistantChannelAccess compares ordinal against.
internal static class SlackUserId
{
    public const string HowToFind =
        "Find it in Slack: click your profile photo, then the three dots (⋮), then \"Copy member ID\".";

    public static bool IsValid(string userId) =>
        userId.Length >= 9
        && userId[0] is 'U' or 'W'
        && userId.Skip(1).All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c));

    public static string? Validate(string userId) =>
        IsValid(userId)
            ? null
            : $"\"{userId}\" is not a Slack member id — it should look like U0123ABCDEF, starting with U or W. {HowToFind}";
}
