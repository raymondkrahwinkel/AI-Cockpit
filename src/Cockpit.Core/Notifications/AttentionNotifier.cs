using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Notifications;

namespace Cockpit.Core.Notifications;

/// <summary>
/// Orchestrates a needs-attention signal: loads the settings, asks <see cref="IPresenceDetector"/>
/// whether the operator is present or away, lets the pure <see cref="NotificationRouter"/> pick the
/// channel, and delivers via the matching notifier. Present → toast, away → Discord webhook. The
/// routing choice is pure and unit-tested separately; this class only wires the pieces together.
/// </summary>
internal sealed class AttentionNotifier(
    INotificationSettingsStore settingsStore,
    IPresenceDetector presenceDetector,
    IToastNotifier toastNotifier,
    IWebhookNotifier webhookNotifier) : IAttentionNotifier, ISingletonService
{
    public async Task NotifyAttentionAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var presence = presenceDetector.GetPresence(settings.IdleThreshold);
        var channel = NotificationRouter.Route(presence, settings.LocalEnabled, settings.DiscordEnabled, settings.HasWebhookUrl);

        switch (channel)
        {
            case NotificationChannel.Toast:
                await toastNotifier.NotifyAsync(notification, cancellationToken).ConfigureAwait(false);
                break;

            case NotificationChannel.Webhook when settings.WebhookUrl is { } webhookUrl:
                await webhookNotifier.NotifyAsync(webhookUrl, notification, cancellationToken).ConfigureAwait(false);
                break;

            case NotificationChannel.None:
                break;
        }
    }
}
