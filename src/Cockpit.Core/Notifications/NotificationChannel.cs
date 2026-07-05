namespace Cockpit.Core.Notifications;

/// <summary>Where a needs-attention notification is delivered, chosen by <see cref="NotificationRouter"/>.</summary>
public enum NotificationChannel
{
    /// <summary>Notifications are disabled — deliver nothing.</summary>
    None,

    /// <summary>Operator is present: an OS-native desktop notification (Windows toast).</summary>
    Toast,

    /// <summary>Operator is away: a Discord webhook POST.</summary>
    Webhook,
}
