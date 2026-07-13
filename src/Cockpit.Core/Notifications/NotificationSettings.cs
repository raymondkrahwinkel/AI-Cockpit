namespace Cockpit.Core.Notifications;

/// <summary>
/// User-configurable presence-notification settings, persisted under the <c>notifications</c>
/// section of <c>cockpit.json</c> (same store pattern as the profiles). Holds the Discord webhook
/// URL used when away, the idle threshold for "away", and the master on/off switch.
/// </summary>
public sealed record NotificationSettings
{
    /// <summary>Default idle time before the operator counts as "away" when the PC is not locked.</summary>
    public static readonly TimeSpan DefaultIdleThreshold = TimeSpan.FromMinutes(15);

    /// <summary>Whether a local OS toast is shown when a session needs attention while you are present. Independent of <see cref="DiscordEnabled"/>.</summary>
    public bool LocalEnabled { get; init; } = true;

    /// <summary>Whether the Discord webhook is POSTed when a session needs attention while you are away. Independent of <see cref="LocalEnabled"/>.</summary>
    public bool DiscordEnabled { get; init; }

    /// <summary>Discord webhook URL POSTed to when away. Null/empty means the away channel is unavailable.</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>Idle time before "away" (when unlocked). Defaults to <see cref="DefaultIdleThreshold"/>.</summary>
    public TimeSpan IdleThreshold { get; init; } = DefaultIdleThreshold;

    /// <summary>Whether a session that finished its turn announces itself when you are not watching it (see <see cref="FinishedNotificationDecision"/>).</summary>
    public bool NotifyOnSessionFinished { get; init; } = true;

    /// <summary>Whether a session that has been finished and quiet for <see cref="SessionIdleThreshold"/> announces that it went idle. Off by default — the interesting moment is usually the answer, not the silence after it.</summary>
    public bool NotifyOnSessionIdle { get; init; }

    /// <summary>Whether one message is sent the moment the last session goes idle, i.e. nothing is running any more.</summary>
    public bool NotifyWhenAllSessionsIdle { get; init; }

    /// <summary>
    /// How long a finished session stays "done" before it counts as idle. Distinct from <see cref="IdleThreshold"/>,
    /// which is about <em>you</em> being away from the PC — this is about a <em>session</em> having nothing to do.
    /// <see cref="TimeSpan.Zero"/> turns the idle transition off.
    /// </summary>
    public TimeSpan SessionIdleThreshold { get; init; } = SessionIdleDecision.DefaultIdleThreshold;

    public bool HasWebhookUrl => !string.IsNullOrWhiteSpace(WebhookUrl);
}
