using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Notifications;

// The toast channel on a platform this build cannot deliver one on — macOS, today. It does nothing rather
// than pretend a notification was shown. Windows has `WindowsToastNotifier`, Linux `LinuxToastNotifier` (#76).
internal sealed class NoOpToastNotifier : IToastNotifier
{
    public Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
