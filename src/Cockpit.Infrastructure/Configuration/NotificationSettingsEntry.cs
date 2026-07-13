using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of <see cref="NotificationSettings"/> in the <c>notifications</c> section of
/// <c>cockpit.json</c>. Stores the idle threshold as whole minutes (the unit the user configures)
/// rather than a serialized <see cref="TimeSpan"/>, so the JSON stays human-editable. Local and Discord
/// notifications are independent switches.
/// </summary>
internal sealed class NotificationSettingsEntry
{
    public bool LocalEnabled { get; set; } = true;

    public bool DiscordEnabled { get; set; }

    public string? WebhookUrl { get; set; }

    public int IdleThresholdMinutes { get; set; } = (int)NotificationSettings.DefaultIdleThreshold.TotalMinutes;

    public bool NotifyOnSessionFinished { get; set; } = true;

    public bool NotifyOnSessionIdle { get; set; }

    public bool NotifyWhenAllSessionsIdle { get; set; }

    /// <summary>Minutes a finished session stays "done" before it counts as idle. 0 turns the idle transition off, so it round-trips as written rather than falling back to the default.</summary>
    public int SessionIdleMinutes { get; set; } = (int)SessionIdleDecision.DefaultIdleThreshold.TotalMinutes;

    public static NotificationSettingsEntry FromDomain(NotificationSettings settings) => new()
    {
        LocalEnabled = settings.LocalEnabled,
        DiscordEnabled = settings.DiscordEnabled,
        WebhookUrl = settings.WebhookUrl,
        IdleThresholdMinutes = (int)settings.IdleThreshold.TotalMinutes,
        NotifyOnSessionFinished = settings.NotifyOnSessionFinished,
        NotifyOnSessionIdle = settings.NotifyOnSessionIdle,
        NotifyWhenAllSessionsIdle = settings.NotifyWhenAllSessionsIdle,
        SessionIdleMinutes = (int)settings.SessionIdleThreshold.TotalMinutes,
    };

    public NotificationSettings ToDomain() => new()
    {
        LocalEnabled = LocalEnabled,
        DiscordEnabled = DiscordEnabled,
        WebhookUrl = WebhookUrl,
        IdleThreshold = IdleThresholdMinutes > 0
            ? TimeSpan.FromMinutes(IdleThresholdMinutes)
            : NotificationSettings.DefaultIdleThreshold,
        NotifyOnSessionFinished = NotifyOnSessionFinished,
        NotifyOnSessionIdle = NotifyOnSessionIdle,
        NotifyWhenAllSessionsIdle = NotifyWhenAllSessionsIdle,
        SessionIdleThreshold = SessionIdleMinutes > 0
            ? TimeSpan.FromMinutes(SessionIdleMinutes)
            : TimeSpan.Zero,
    };
}
