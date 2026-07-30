using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// A notifier that cannot deliver — a desktop with no notification service, a Windows toast API that refuses. Its
/// job in a test is to prove that a credential is not lost because the machine could not show a message about it.
/// </summary>
internal sealed class ThrowingToastNotifier : IToastNotifier
{
    public Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No notification service on this desktop.");
}
