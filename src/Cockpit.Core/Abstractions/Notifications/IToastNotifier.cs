using Cockpit.Core.Notifications;

namespace Cockpit.Core.Abstractions.Notifications;

/// <summary>
/// Delivers an OS-native desktop notification (Windows toast) for the present operator.
/// </summary>
public interface IToastNotifier
{
    Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows <paramref name="notification"/> without a sound and waits for the operator to click it (AC-1588); true
    /// when they did, false when it was dismissed, timed out or this platform cannot report a click. Default: an
    /// ordinary <see cref="NotifyAsync"/> that never reports a click.
    /// </summary>
    async Task<bool> NotifySilentlyAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        await NotifyAsync(notification, cancellationToken).ConfigureAwait(false);
        return false;
    }
}
