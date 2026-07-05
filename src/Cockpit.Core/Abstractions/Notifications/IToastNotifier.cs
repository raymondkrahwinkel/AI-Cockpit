using Cockpit.Core.Notifications;

namespace Cockpit.Core.Abstractions.Notifications;

/// <summary>Delivers an OS-native desktop notification (Windows toast) for the present operator.</summary>
public interface IToastNotifier
{
    Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default);
}
