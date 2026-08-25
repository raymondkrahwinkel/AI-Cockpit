namespace Cockpit.Core.Notifications;

// Pure routing kernel: maps a `PresenceState` to the channel a needs-attention notification
// should take — present → toast, away → webhook. Kept OS-free so the decision is unit-testable
// in isolation from the detector and notifier implementations.
public static class NotificationRouter
{
    // Present routes to toast only when `localEnabled` is on; away routes to webhook only when
    // `discordEnabled` is on and a URL is configured. Otherwise `NotificationChannel.None`.
    public static NotificationChannel Route(PresenceState presence, bool localEnabled, bool discordEnabled, bool hasWebhookUrl)
    {
        return presence switch
        {
            PresenceState.Away => discordEnabled && hasWebhookUrl ? NotificationChannel.Webhook : NotificationChannel.None,
            _ => localEnabled ? NotificationChannel.Toast : NotificationChannel.None,
        };
    }
}
