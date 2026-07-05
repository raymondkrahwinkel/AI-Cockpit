namespace Cockpit.Core.Notifications;

/// <summary>
/// Pure routing kernel: maps a <see cref="PresenceState"/> to the channel a needs-attention
/// notification should take — present → toast, away → webhook. Kept OS-free so the
/// present-toast / away-webhook decision is unit-testable in isolation from the detector and the
/// notifier implementations.
/// </summary>
public static class NotificationRouter
{
    /// <summary>
    /// Chooses the delivery channel. When notifications are disabled the result is always
    /// <see cref="NotificationChannel.None"/>, regardless of presence. The away channel falls back
    /// to <see cref="NotificationChannel.None"/> when no webhook URL is configured, so an away
    /// operator without a webhook is not silently routed to a toast they cannot see.
    /// </summary>
    public static NotificationChannel Route(PresenceState presence, bool isEnabled, bool hasWebhookUrl)
    {
        if (!isEnabled)
        {
            return NotificationChannel.None;
        }

        return presence switch
        {
            PresenceState.Away => hasWebhookUrl ? NotificationChannel.Webhook : NotificationChannel.None,
            _ => NotificationChannel.Toast,
        };
    }
}
