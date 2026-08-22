namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>
/// Who may talk to the assistant over a channel (AC-1023 §3). Three levels rather than one allow-list, because a
/// few people you know and everyone at all are different risks.
/// </summary>
public enum AssistantChannelAudience
{
    /// <summary>
    /// The default: exactly one platform account, with anything from any other account ignored outright.
    /// </summary>
    SingleUser,

    /// <summary>
    /// A named list of accounts, reached only by acknowledging <see cref="AssistantChannelAccess.MultipleUsersWarning"/>.
    /// </summary>
    SpecificUsers,

    /// <summary>
    /// Anyone at all, reached only by typing <see cref="AssistantChannelAccess.EveryoneConfirmationPhrase"/> over.
    /// </summary>
    Everyone,
}

/// <summary>
/// Who may talk to the assistant over one channel instance (AC-1023 §3). Buildable only through the three
/// factories, so no level above <see cref="AssistantChannelAudience.SingleUser"/> can exist unwarned.
/// </summary>
public sealed class AssistantChannelAccess
{
    /// <summary>
    /// Shown once when widening to <see cref="AssistantChannelAudience.SpecificUsers"/>.
    /// </summary>
    public const string MultipleUsersWarning =
        "Everyone on this list can talk to your assistant as if they were you. You are responsible for checking " +
        "whether that goes against the terms of service of the platform you are connecting.";

    /// <summary>
    /// Shown when widening to <see cref="AssistantChannelAudience.Everyone"/> — heavier, and not passable by clicking.
    /// </summary>
    public const string EveryoneWarning =
        "Anyone at all will be able to talk to your assistant as if they were you, including people you have never " +
        "met. That is a different risk from letting in a few people you know, and you are responsible for checking " +
        "whether it goes against the terms of service of the platform you are connecting. Type the confirmation " +
        "phrase to continue.";

    /// <summary>
    /// The sentence to type over to reach <see cref="AssistantChannelAudience.Everyone"/>. A speed bump rather than
    /// a password: matched whole, ignoring case and surrounding space.
    /// </summary>
    public const string EveryoneConfirmationPhrase = "anyone may talk to my assistant";

    private readonly HashSet<string> _userIds;

    private AssistantChannelAccess(AssistantChannelAudience audience, IEnumerable<string> userIds)
    {
        Audience = audience;
        _userIds = new HashSet<string>(userIds, StringComparer.Ordinal);
    }

    /// <summary>
    /// Which of the three levels this is.
    /// </summary>
    public AssistantChannelAudience Audience { get; }

    /// <summary>
    /// The accounts allowed by name, empty for <see cref="AssistantChannelAudience.Everyone"/>.
    /// </summary>
    public IReadOnlyCollection<string> UserIds => _userIds;

    /// <summary>
    /// The default level: one account, nothing to acknowledge, everyone else ignored.
    /// </summary>
    public static AssistantChannelAccessResult ForSingleUser(string userId) =>
        string.IsNullOrWhiteSpace(userId)
            ? AssistantChannelAccessResult.Refused("A channel needs the user id of the one account allowed to talk to the assistant.")
            : AssistantChannelAccessResult.Configured(new AssistantChannelAccess(AssistantChannelAudience.SingleUser, [userId.Trim()]));

    /// <summary>
    /// Widens to a named list. A false <paramref name="warningAcknowledged"/> is refused with
    /// <see cref="MultipleUsersWarning"/> as the reason, so the warning cannot be skipped.
    /// </summary>
    public static AssistantChannelAccessResult ForUsers(IReadOnlyList<string> userIds, bool warningAcknowledged)
    {
        var named = (userIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList();

        if (named.Count == 0)
        {
            return AssistantChannelAccessResult.Refused("A channel needs at least one user id allowed to talk to the assistant.");
        }

        return warningAcknowledged
            ? AssistantChannelAccessResult.Configured(new AssistantChannelAccess(AssistantChannelAudience.SpecificUsers, named))
            : AssistantChannelAccessResult.Refused(MultipleUsersWarning);
    }

    /// <summary>
    /// Widens to everyone. Anything but <see cref="EveryoneConfirmationPhrase"/> typed over is refused with
    /// <see cref="EveryoneWarning"/> as the reason.
    /// </summary>
    public static AssistantChannelAccessResult ForEveryone(string typedConfirmation) =>
        string.Equals(typedConfirmation?.Trim(), EveryoneConfirmationPhrase, StringComparison.OrdinalIgnoreCase)
            ? AssistantChannelAccessResult.Configured(new AssistantChannelAccess(AssistantChannelAudience.Everyone, []))
            : AssistantChannelAccessResult.Refused(EveryoneWarning);

    /// <summary>
    /// Whether a message from <paramref name="userId"/> may be acted on. A false is silence, never a reply — telling
    /// a stranger they are not allowed confirms the bot is listening.
    /// </summary>
    public bool IsAllowed(string? userId) =>
        !string.IsNullOrWhiteSpace(userId)
        && (Audience == AssistantChannelAudience.Everyone || _userIds.Contains(userId.Trim()));

    // AC-1023: the one door that skips the warnings, internal because it is only right for reading back a decision
    // the operator already made.
    internal static AssistantChannelAccess Restore(AssistantChannelAudience audience, IEnumerable<string> userIds) =>
        new(audience, userIds);
}

/// <summary>
/// What came of choosing an access level (AC-1023). A refusal is a sentence the settings screen shows, not an
/// exception — the same shape as the app's other gateways.
/// </summary>
public sealed record AssistantChannelAccessResult(bool Ok, AssistantChannelAccess? Access, string? Error)
{
    /// <summary>
    /// The level stands, and <paramref name="access"/> is what to store and hand to a contribution.
    /// </summary>
    public static AssistantChannelAccessResult Configured(AssistantChannelAccess access) => new(true, access, null);

    /// <summary>
    /// It does not stand, and <paramref name="error"/> is why — shown to the operator verbatim.
    /// </summary>
    public static AssistantChannelAccessResult Refused(string error) => new(false, null, error);
}
