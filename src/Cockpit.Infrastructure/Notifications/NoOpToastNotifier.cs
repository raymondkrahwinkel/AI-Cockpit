using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Notifications;

/// <summary>
/// The toast channel on a platform this build cannot deliver one on — macOS, today. It does nothing rather than
/// pretend a notification was shown, which is the honest half of the arrangement; the other half is that nobody here
/// has a Mac to try a real one on, and a notifier that has never been seen to fire is a claim rather than a feature.
/// Windows has <c>WindowsToastNotifier</c>, Linux <c>LinuxToastNotifier</c> (#76).
/// </summary>
internal sealed class NoOpToastNotifier : IToastNotifier
{
    public Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
