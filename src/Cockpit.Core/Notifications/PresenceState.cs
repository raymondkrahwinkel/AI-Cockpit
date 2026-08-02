namespace Cockpit.Core.Notifications;

// Whether the operator is at the PC or away, as decided by `PresenceDecision` from
// the idle time and lock state. Drives which channel a needs-attention notification takes.
public enum PresenceState
{
    // At the PC: recent input and the session is not locked.
    Present,

    // Away: idle past the threshold, or the workstation is locked.
    Away,
}
