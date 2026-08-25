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

    public static string? Validate(string userId)
    {
        if (IsValid(userId))
        {
            return null;
        }

        var opening = _Describe(userId) is { } what
            ? $"\"{userId}\" is {what}, not a Slack member id"
            : $"\"{userId}\" is not a Slack member id";

        return $"{opening} — it should look like U0123ABCDEF, starting with U or W. {HowToFind}";
    }

    // AC-1074: the first error in a stored access list, or null when every id has the right shape.
    public static string? ValidateAll(IEnumerable<string> userIds) =>
        userIds.Select(Validate).FirstOrDefault(error => error is not null);

    // AC-1074: which Slack object was pasted instead. Being told "that is a DM conversation id" is the difference
    // between fixing it and pasting the same value back in.
    private static string? _Describe(string userId) => userId.Length < 9 ? null : userId[0] switch
    {
        'D' => "a DM conversation id",
        'C' => "a public channel id",
        'G' => "a private channel id",
        'B' => "a bot id",
        'T' => "a workspace id",
        _ => null,
    };
}
