namespace Cockpit.Core.Notifications;

// Where a needs-attention notification is delivered, chosen by `NotificationRouter`.
public enum NotificationChannel
{
    // Notifications are disabled — deliver nothing.
    None,

    // Operator is present: an OS-native desktop notification (Windows toast).
    Toast,

    // Operator is away: a Discord webhook POST.
    Webhook,
}
