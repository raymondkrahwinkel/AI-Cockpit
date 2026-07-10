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
    /// Chooses the delivery channel from the two independent switches. Present routes to a local toast
    /// only when <paramref name="localEnabled"/> is on; away routes to the Discord webhook only when
    /// <paramref name="discordEnabled"/> is on and a webhook URL is configured. Either being off (or no
    /// webhook when away) yields <see cref="NotificationChannel.None"/>.
    /// </summary>
    public static NotificationChannel Route(PresenceState presence, bool localEnabled, bool discordEnabled, bool hasWebhookUrl)
    {
        return presence switch
        {
            PresenceState.Away => discordEnabled && hasWebhookUrl ? NotificationChannel.Webhook : NotificationChannel.None,
            _ => localEnabled ? NotificationChannel.Toast : NotificationChannel.None,
        };
    }
}
