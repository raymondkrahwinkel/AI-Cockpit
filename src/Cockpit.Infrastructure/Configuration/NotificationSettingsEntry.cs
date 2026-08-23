using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `NotificationSettings`. Idle threshold is stored as whole minutes, not a serialized
// `TimeSpan`, so the JSON stays human-editable. Local and Discord notifications are independent switches.
internal sealed class NotificationSettingsEntry
{
    public bool LocalEnabled { get; set; } = true;

    public bool DiscordEnabled { get; set; }

    public string? WebhookUrl { get; set; }

    public int IdleThresholdMinutes { get; set; } = (int)NotificationSettings.DefaultIdleThreshold.TotalMinutes;

    public bool NotifyOnSessionFinished { get; set; } = true;

    public bool NotifyOnSessionIdle { get; set; }

    public bool NotifyWhenAllSessionsIdle { get; set; }

    public bool NotifyOnCiFailure { get; set; } = true;

    // Minutes a finished session stays "done" before it counts as idle. 0 turns the idle transition off, so it round-trips as written rather than falling back to the default.
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
        NotifyOnCiFailure = settings.NotifyOnCiFailure,
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
        NotifyOnCiFailure = NotifyOnCiFailure,
        SessionIdleThreshold = SessionIdleMinutes > 0
            ? TimeSpan.FromMinutes(SessionIdleMinutes)
            : TimeSpan.Zero,
    };
}
