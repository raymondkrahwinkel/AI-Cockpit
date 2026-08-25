using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Notifications;

namespace Cockpit.Core.Notifications;

// Orchestrates the session signals: asks `IPresenceDetector` for present/away, lets the pure
// `NotificationRouter` pick the channel, and delivers via the matching notifier (toast/webhook).
// Each signal has its own gate, but they share one delivery path.
internal sealed class AttentionNotifier(
    INotificationSettingsStore settingsStore,
    IPresenceDetector presenceDetector,
    IToastNotifier toastNotifier,
    IWebhookNotifier webhookNotifier) : IAttentionNotifier, ISingletonService
{
    public async Task NotifyAttentionAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _DeliverAsync(notification, settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifySessionFinishedAsync(AttentionNotification notification, bool isSelected, bool isWindowActive, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.NotifyOnSessionFinished)
        {
            return;
        }

        var presence = presenceDetector.GetPresence(settings.IdleThreshold);
        if (!FinishedNotificationDecision.ShouldNotify(isSelected, isWindowActive, presence))
        {
            return;
        }

        await _DeliverAsync(notification, settings, presence, cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifySessionIdleAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.NotifyOnSessionIdle)
        {
            return;
        }

        await _DeliverAsync(notification, settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyAllSessionsIdleAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.NotifyWhenAllSessionsIdle)
        {
            return;
        }

        var notification = new AttentionNotification("Cockpit", "All sessions are idle");
        await _DeliverAsync(notification, settings, cancellationToken).ConfigureAwait(false);
    }

    private Task _DeliverAsync(AttentionNotification notification, NotificationSettings settings, CancellationToken cancellationToken) =>
        _DeliverAsync(notification, settings, presenceDetector.GetPresence(settings.IdleThreshold), cancellationToken);

    private async Task _DeliverAsync(AttentionNotification notification, NotificationSettings settings, PresenceState presence, CancellationToken cancellationToken)
    {
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
