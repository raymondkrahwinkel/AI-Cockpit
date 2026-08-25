namespace Cockpit.Core.Notifications;

// Pure kernel deciding whether a finished session should announce itself. A session you are
// watching (selected, window in front) doesn't need to — you saw it. Any other case means the
// result would otherwise go unnoticed, which is the point of the notification.
public static class FinishedNotificationDecision
{
    // `isSelected`: The finished session is the one selected in the cockpit.
    // `isWindowActive`: The cockpit window is the active (focused) window.
    // `presence`: Whether the operator is at the PC at all.
    public static bool ShouldNotify(bool isSelected, bool isWindowActive, PresenceState presence) =>
        presence == PresenceState.Away || !isSelected || !isWindowActive;
}
