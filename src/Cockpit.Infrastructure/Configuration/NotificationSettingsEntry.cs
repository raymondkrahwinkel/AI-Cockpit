using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of <see cref="NotificationSettings"/> in the <c>notifications</c> section of
/// <c>cockpit.json</c>. Stores the idle threshold as whole minutes (the unit the user configures)
/// rather than a serialized <see cref="TimeSpan"/>, so the JSON stays human-editable.
/// </summary>
internal sealed class NotificationSettingsEntry
{
    public bool IsEnabled { get; set; } = true;

    public string? WebhookUrl { get; set; }

    public int IdleThresholdMinutes { get; set; } = (int)NotificationSettings.DefaultIdleThreshold.TotalMinutes;

    public static NotificationSettingsEntry FromDomain(NotificationSettings settings) => new()
    {
        IsEnabled = settings.IsEnabled,
        WebhookUrl = settings.WebhookUrl,
        IdleThresholdMinutes = (int)settings.IdleThreshold.TotalMinutes,
    };

    public NotificationSettings ToDomain() => new()
    {
        IsEnabled = IsEnabled,
        WebhookUrl = WebhookUrl,
        IdleThreshold = IdleThresholdMinutes > 0
            ? TimeSpan.FromMinutes(IdleThresholdMinutes)
            : NotificationSettings.DefaultIdleThreshold,
    };
}
