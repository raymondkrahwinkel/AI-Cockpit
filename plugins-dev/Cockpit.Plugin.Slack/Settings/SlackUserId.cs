namespace Cockpit.Plugin.Slack.Settings;

// AC-1048: `AssistantChannelAccess` compares ordinal against whatever the platform hands over as a sender, which
// for Slack is a member id like `U0123ABCDEF` — never a display name. Slack shows that id nowhere prominent, so a
// rejected value points at where to find it, not just that it is wrong. Platform-specific on purpose: the shared
// abstraction (AssistantChannelAccess) stays agnostic to what a "user id" looks like.
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
