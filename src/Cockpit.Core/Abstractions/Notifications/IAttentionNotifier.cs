using Cockpit.Core.Notifications;

namespace Cockpit.Core.Abstractions.Notifications;

/// <summary>
/// Entry point for the "a session needs attention" signal: decides presence, routes to toast or
/// webhook, and delivers. The cockpit calls this on an edge-triggered transition into
/// <c>NeedsAttention</c>; everything downstream (presence, routing, channel) is handled here.
/// </summary>
public interface IAttentionNotifier
{
    Task NotifyAttentionAsync(AttentionNotification notification, CancellationToken cancellationToken = default);
}
