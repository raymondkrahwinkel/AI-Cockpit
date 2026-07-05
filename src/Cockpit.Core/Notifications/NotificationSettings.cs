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

    /// <summary>Master switch. When false, no notification is delivered on either channel.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Discord webhook URL POSTed to when away. Null/empty means the away channel is unavailable.</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>Idle time before "away" (when unlocked). Defaults to <see cref="DefaultIdleThreshold"/>.</summary>
    public TimeSpan IdleThreshold { get; init; } = DefaultIdleThreshold;

    public bool HasWebhookUrl => !string.IsNullOrWhiteSpace(WebhookUrl);
}
