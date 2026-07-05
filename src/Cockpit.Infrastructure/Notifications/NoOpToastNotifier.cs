using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Notifications;

/// <summary>
/// Non-Windows fallback toast. FOLLOW-UP / NOT IMPLEMENTED: a native Linux desktop notification
/// (libnotify / D-Bus) is not built yet. Registered only on non-Windows platforms (see DI); does
/// nothing rather than pretend a toast was shown. On Windows <c>WindowsToastNotifier</c> is used.
/// </summary>
internal sealed class NoOpToastNotifier : IToastNotifier
{
    public Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
