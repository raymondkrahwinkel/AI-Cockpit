namespace Cockpit.Core.Notifications;

/// <summary>
/// Whether the operator is at the PC or away, as decided by <see cref="PresenceDecision"/> from
/// the idle time and lock state. Drives which channel a needs-attention notification takes.
/// </summary>
public enum PresenceState
{
    /// <summary>At the PC: recent input and the session is not locked.</summary>
    Present,

    /// <summary>Away: idle past the threshold, or the workstation is locked.</summary>
    Away,
}
