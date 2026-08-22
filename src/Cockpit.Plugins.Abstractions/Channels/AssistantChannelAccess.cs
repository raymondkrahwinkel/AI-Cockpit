namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>
/// Who may talk to the assistant over a channel (AC-1023 §3). Three levels rather than one allow-list: letting a
/// few people you know in and opening the door to everyone are different risks, and each earns its own warning
/// instead of one averaged one.
/// </summary>
public enum AssistantChannelAudience
{
    /// <summary>The default: exactly one platform account. Anything from any other account is ignored outright.</summary>
    SingleUser,

    /// <summary>A named list of accounts. Reaching it means acknowledging <see cref="AssistantChannelAccess.MultipleUsersWarning"/>.</summary>
    SpecificUsers,

    /// <summary>Anyone at all. Reaching it means typing <see cref="AssistantChannelAccess.EveryoneConfirmationPhrase"/> over.</summary>
    Everyone,
}

/// <summary>
/// Who may talk to the assistant over one channel instance, and the gate that has to be passed to widen it
/// (AC-1023 §3). Buildable only through the three factories, so a level above <see cref="AssistantChannelAudience.SingleUser"/>
/// cannot come into existence without the operator having been shown its warning first.
/// </summary>
/// <remarks>
/// What a user id <em>is</em> stays the plugin's business — a Discord snowflake, a Slack member id. This type only
/// stores them and answers <see cref="IsAllowed"/>, so the host can enforce the level without knowing any platform.
/// </remarks>
public sealed class AssistantChannelAccess
{
    /// <summary>Shown once when widening to <see cref="AssistantChannelAudience.SpecificUsers"/>.</summary>
    public const string MultipleUsersWarning =
        "Everyone on this list can talk to your assistant as if they were you. You are responsible for checking " +
        "whether that goes against the terms of service of the platform you are connecting.";

    /// <summary>Shown when widening to <see cref="AssistantChannelAudience.Everyone"/> — heavier than <see cref="MultipleUsersWarning"/>, and not passable by clicking.</summary>
    public const string EveryoneWarning =
        "Anyone at all will be able to talk to your assistant as if they were you, including people you have never " +
        "met. That is a different risk from letting in a few people you know, and you are responsible for checking " +
        "whether it goes against the terms of service of the platform you are connecting. Type the confirmation " +
        "phrase to continue.";

    /// <summary>The sentence that has to be typed over to reach <see cref="AssistantChannelAudience.Everyone"/>. A speed bump, not a password: matched whole, ignoring case and surrounding space.</summary>
    public const string EveryoneConfirmationPhrase = "anyone may talk to my assistant";

    private readonly HashSet<string> _userIds;

    private AssistantChannelAccess(AssistantChannelAudience audience, IEnumerable<string> userIds)
    {
        Audience = audience;
        _userIds = new HashSet<string>(userIds, StringComparer.Ordinal);
    }

    /// <summary>Which of the three levels this is.</summary>
    public AssistantChannelAudience Audience { get; }

    /// <summary>The accounts allowed by name. Empty for <see cref="AssistantChannelAudience.Everyone"/>, which names nobody.</summary>
    public IReadOnlyCollection<string> UserIds => _userIds;

    /// <summary>The default level: one account, no warning to acknowledge, everything from anyone else ignored.</summary>
    public static AssistantChannelAccessResult ForSingleUser(string userId) =>
        string.IsNullOrWhiteSpace(userId)
            ? AssistantChannelAccessResult.Refused("A channel needs the user id of the one account allowed to talk to the assistant.")
            : AssistantChannelAccessResult.Configured(new AssistantChannelAccess(AssistantChannelAudience.SingleUser, [userId.Trim()]));

    /// <summary>
    /// Widens to a named list. <paramref name="warningAcknowledged"/> is the operator having read
    /// <see cref="MultipleUsersWarning"/> — false is refused with that warning as the reason, so it cannot be skipped.
    /// </summary>
    public static AssistantChannelAccessResult ForUsers(IReadOnlyList<string> userIds, bool warningAcknowledged)
    {
        var trimmed = (userIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToList();

        if (trimmed.Count == 0)
        {
            return AssistantChannelAccessResult.Refused("A channel needs at least one user id allowed to talk to the assistant.");
        }

        return warningAcknowledged
            ? AssistantChannelAccessResult.Configured(new AssistantChannelAccess(AssistantChannelAudience.SpecificUsers, trimmed))
            : AssistantChannelAccessResult.Refused(MultipleUsersWarning);
    }

    /// <summary>
    /// Widens to everyone. <paramref name="typedConfirmation"/> has to be <see cref="EveryoneConfirmationPhrase"/>
    /// typed over — anything else is refused with <see cref="EveryoneWarning"/> as the reason.
    /// </summary>
    public static AssistantChannelAccessResult ForEveryone(string typedConfirmation) =>
        string.Equals(typedConfirmation?.Trim(), EveryoneConfirmationPhrase, StringComparison.OrdinalIgnoreCase)
            ? AssistantChannelAccessResult.Configured(new AssistantChannelAccess(AssistantChannelAudience.Everyone, []))
            : AssistantChannelAccessResult.Refused(EveryoneWarning);

    /// <summary>
    /// Whether a message from <paramref name="userId"/> may be acted on. A false is silence, never a reply: telling a
    /// stranger they are not allowed confirms the bot is listening, which is what §3 keeps from happening.
    /// </summary>
    public bool IsAllowed(string? userId) =>
        !string.IsNullOrWhiteSpace(userId)
        && (Audience == AssistantChannelAudience.Everyone || _userIds.Contains(userId.Trim()));

    // Rebuilds a level the operator already passed the gate for, from where it was stored. Internal because it is
    // the one door that skips the warnings, and it is only right for reading back a decision already made.
    internal static AssistantChannelAccess Restore(AssistantChannelAudience audience, IEnumerable<string> userIds) =>
        new(audience, userIds);
}

/// <summary>
/// What came of choosing an access level. A refusal is a sentence the settings screen shows — the warning that was
/// not acknowledged, or the field that was left empty — not an exception, the same shape as the app's other gateways.
/// </summary>
public sealed record AssistantChannelAccessResult(bool Ok, AssistantChannelAccess? Access, string? Error)
{
    /// <summary>The level stands; <paramref name="access"/> is what to store and hand to a contribution.</summary>
    public static AssistantChannelAccessResult Configured(AssistantChannelAccess access) => new(true, access, null);

    /// <summary>It does not stand, and <paramref name="error"/> is why — shown to the operator verbatim.</summary>
    public static AssistantChannelAccessResult Refused(string error) => new(false, null, error);
}
