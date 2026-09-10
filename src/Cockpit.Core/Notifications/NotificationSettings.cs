namespace Cockpit.Core.Notifications;

// User-configurable presence-notification settings, persisted under the `notifications`
// section of `cockpit.json` (same store pattern as the profiles). Holds the Discord webhook
// URL used when away, the idle threshold for "away", and the master on/off switch.
public sealed record NotificationSettings
{
    // Default idle time before the operator counts as "away" when the PC is not locked.
    public static readonly TimeSpan DefaultIdleThreshold = TimeSpan.FromMinutes(15);

    // Whether a local OS toast is shown when a session needs attention while you are present. Independent of `DiscordEnabled`.
    public bool LocalEnabled { get; init; } = true;

    // Whether the Discord webhook is POSTed when a session needs attention while you are away. Independent of `LocalEnabled`.
    public bool DiscordEnabled { get; init; }

    // Discord webhook URL POSTed to when away. Null/empty means the away channel is unavailable.
    public string? WebhookUrl { get; init; }

    // Idle time before "away" (when unlocked). Defaults to `DefaultIdleThreshold`.
    public TimeSpan IdleThreshold { get; init; } = DefaultIdleThreshold;

    // Whether a session that finished its turn announces itself when you are not watching it (see `FinishedNotificationDecision`).
    public bool NotifyOnSessionFinished { get; init; } = true;

    // Whether a session that has been finished and quiet for `SessionIdleThreshold` announces that it went idle. Off by default — the interesting moment is usually the answer, not the silence after it.
    public bool NotifyOnSessionIdle { get; init; }

    // Whether one message is sent the moment the last session goes idle, i.e. nothing is running any more.
    public bool NotifyWhenAllSessionsIdle { get; init; }

    // AC-634: whether the branch a session is working on is watched for a failing CI check. Off means no `gh` is run
    // at all, not merely that the answer is swallowed.
    public bool NotifyOnCiFailure { get; init; } = true;

    // AC-1312: how many messages the background session monitor may send the assistant in one hour. Each one wakes
    // it (`InboxWakeScheduler`), so this is the ceiling on turns a pathological run can cost. Findings over the
    // ceiling are held, not dropped — the next tick under it says them.
    public const int DefaultMonitorMessagesPerHour = 12;

    public int MonitorMessagesPerHour { get; init; } = DefaultMonitorMessagesPerHour;

    // AC-1313: how long a session may write nothing before the monitor says so. Measured on transcript rows and
    // never on status, so it keeps working on a session whose status is wrong — which is the case it is for. A
    // session may ask for more room (`quietFor`) and the operator may give one more or mute it outright.
    public static readonly TimeSpan DefaultMonitorSilenceThreshold = TimeSpan.FromMinutes(10);

    public TimeSpan MonitorSilenceThreshold { get; init; } = DefaultMonitorSilenceThreshold;

    // How long a finished session stays "done" before it counts as idle. Distinct from `IdleThreshold`,
    // which is about *you* being away from the PC — this is about a *session* having nothing to do.
    // `TimeSpan.Zero` turns the idle transition off.
    public TimeSpan SessionIdleThreshold { get; init; } = SessionIdleDecision.DefaultIdleThreshold;

    public bool HasWebhookUrl => !string.IsNullOrWhiteSpace(WebhookUrl);
}
