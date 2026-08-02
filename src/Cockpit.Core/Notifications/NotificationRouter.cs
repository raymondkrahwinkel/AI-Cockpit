namespace Cockpit.Core.Notifications;

// Pure routing kernel: maps a `PresenceState` to the channel a needs-attention
// notification should take — present → toast, away → webhook. Kept OS-free so the
// present-toast / away-webhook decision is unit-testable in isolation from the detector and the
// notifier implementations.
public static class NotificationRouter
{
    // Chooses the delivery channel from the two independent switches. Present routes to a local toast
    // only when `localEnabled` is on; away routes to the Discord webhook only when
    // `discordEnabled` is on and a webhook URL is configured. Either being off (or no
    // webhook when away) yields `NotificationChannel.None`.
    public static NotificationChannel Route(PresenceState presence, bool localEnabled, bool discordEnabled, bool hasWebhookUrl)
    {
        return presence switch
        {
            PresenceState.Away => discordEnabled && hasWebhookUrl ? NotificationChannel.Webhook : NotificationChannel.None,
            _ => localEnabled ? NotificationChannel.Toast : NotificationChannel.None,
        };
    }
}
