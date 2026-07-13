namespace Cockpit.Core.Notifications;

/// <summary>
/// Pure kernel deciding whether a session that just finished its turn should announce itself. A session you are
/// watching does not need to: it is selected and the window is in front, so you saw the answer arrive. Every
/// other case — another session selected, the window behind something else, or you away from the PC entirely —
/// means the result would otherwise go unnoticed, which is the whole point of the notification.
/// </summary>
public static class FinishedNotificationDecision
{
    /// <param name="isSelected">The finished session is the one selected in the cockpit.</param>
    /// <param name="isWindowActive">The cockpit window is the active (focused) window.</param>
    /// <param name="presence">Whether the operator is at the PC at all.</param>
    public static bool ShouldNotify(bool isSelected, bool isWindowActive, PresenceState presence) =>
        presence == PresenceState.Away || !isSelected || !isWindowActive;
}
