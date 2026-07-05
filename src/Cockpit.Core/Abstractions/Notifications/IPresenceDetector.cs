using Cockpit.Core.Notifications;

namespace Cockpit.Core.Abstractions.Notifications;

/// <summary>
/// Reports whether the operator is present at the PC or away, given an idle threshold. The
/// OS-specific measurement (idle time, lock state) lives in the implementation; the present/away
/// call itself is delegated to the pure <see cref="PresenceDecision"/> kernel so it stays testable.
/// </summary>
public interface IPresenceDetector
{
    PresenceState GetPresence(TimeSpan idleThreshold);
}
