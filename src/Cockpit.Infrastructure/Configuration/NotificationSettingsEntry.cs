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

    // Messages an hour the background session monitor may send the assistant. 0 or less reads as the default rather
    // than as silence: a hand-cleared value should not switch the safety net off without saying so.
    public int MonitorMessagesPerHour { get; set; } = NotificationSettings.DefaultMonitorMessagesPerHour;

    // Minutes a session may write nothing before the monitor reports it. 0 or less reads as the default for the
    // same reason as above — a cleared value must not silence the safety net without saying so.
    public int MonitorSilenceMinutes { get; set; } = (int)NotificationSettings.DefaultMonitorSilenceThreshold.TotalMinutes;

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
        MonitorMessagesPerHour = settings.MonitorMessagesPerHour,
        MonitorSilenceMinutes = (int)settings.MonitorSilenceThreshold.TotalMinutes,
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
        MonitorMessagesPerHour = MonitorMessagesPerHour > 0
            ? MonitorMessagesPerHour
            : NotificationSettings.DefaultMonitorMessagesPerHour,
        MonitorSilenceThreshold = MonitorSilenceMinutes > 0
            ? TimeSpan.FromMinutes(MonitorSilenceMinutes)
            : NotificationSettings.DefaultMonitorSilenceThreshold,
        SessionIdleThreshold = SessionIdleMinutes > 0
            ? TimeSpan.FromMinutes(SessionIdleMinutes)
            : TimeSpan.Zero,
    };
}
