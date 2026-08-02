namespace Cockpit.Core.Notifications;

// Pure kernel deciding whether a session that just finished its turn should announce itself. A session you are
// watching does not need to: it is selected and the window is in front, so you saw the answer arrive. Every
// other case — another session selected, the window behind something else, or you away from the PC entirely —
// means the result would otherwise go unnoticed, which is the whole point of the notification.
public static class FinishedNotificationDecision
{
    // `isSelected`: The finished session is the one selected in the cockpit.
    // `isWindowActive`: The cockpit window is the active (focused) window.
    // `presence`: Whether the operator is at the PC at all.
    public static bool ShouldNotify(bool isSelected, bool isWindowActive, PresenceState presence) =>
        presence == PresenceState.Away || !isSelected || !isWindowActive;
}
