using Cockpit.Core.Notifications;

namespace Cockpit.Core.Abstractions.Notifications;

/// <summary>
/// Entry point for every "something in a session wants you to know" signal: decides presence, routes to toast or
/// webhook, and delivers. Called on edge-triggered transitions — into <c>NeedsAttention</c>, <c>Done</c>, idle —
/// so a caller never has to know which channel a message ends up taking.
/// </summary>
public interface IAttentionNotifier
{
    /// <summary>
    /// A session needs a decision from you: a permission prompt, or the CLI reporting <c>needs_action</c>.
    /// </summary>
    Task NotifyAttentionAsync(AttentionNotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// A session finished its turn — delivered only when you'd miss it; see <see cref="FinishedNotificationDecision"/>.
    /// </summary>
    /// <param name="isSelected">
    /// The finished session is the one currently selected.
    /// </param>
    /// <param name="isWindowActive">
    /// The cockpit window is the focused window.
    /// </param>
    Task NotifySessionFinishedAsync(AttentionNotification notification, bool isSelected, bool isWindowActive, CancellationToken cancellationToken = default);

    /// <summary>
    /// A session has been finished and quiet long enough to count as idle. Delivered only when the operator asked for it.
    /// </summary>
    Task NotifySessionIdleAsync(AttentionNotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// The last working session went idle: nothing is running any more. Delivered only when the operator asked for it.
    /// </summary>
    Task NotifyAllSessionsIdleAsync(CancellationToken cancellationToken = default);
}
