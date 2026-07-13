using Cockpit.Core.Notifications;

namespace Cockpit.Core.Abstractions.Notifications;

/// <summary>
/// Entry point for every "something in a session wants you to know" signal: decides presence, routes to toast
/// or webhook, and delivers. The cockpit calls these on edge-triggered transitions — into <c>NeedsAttention</c>,
/// into <c>Done</c>, into idle — and everything downstream (presence, routing, channel, the operator's toggles)
/// is handled here, so a caller never has to know which channel a message ends up taking.
/// </summary>
public interface IAttentionNotifier
{
    /// <summary>A session needs a decision from you: a permission prompt, or the CLI reporting <c>needs_action</c>.</summary>
    Task NotifyAttentionAsync(AttentionNotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// A session finished its turn. Delivered only when you would otherwise miss it — see
    /// <see cref="FinishedNotificationDecision"/>: a selected session in a focused window already showed you
    /// the answer arriving.
    /// </summary>
    /// <param name="isSelected">The finished session is the one currently selected.</param>
    /// <param name="isWindowActive">The cockpit window is the focused window.</param>
    Task NotifySessionFinishedAsync(AttentionNotification notification, bool isSelected, bool isWindowActive, CancellationToken cancellationToken = default);

    /// <summary>A session has been finished and quiet long enough to count as idle. Delivered only when the operator asked for it.</summary>
    Task NotifySessionIdleAsync(AttentionNotification notification, CancellationToken cancellationToken = default);

    /// <summary>The last working session went idle: nothing is running any more. Delivered only when the operator asked for it.</summary>
    Task NotifyAllSessionsIdleAsync(CancellationToken cancellationToken = default);
}
