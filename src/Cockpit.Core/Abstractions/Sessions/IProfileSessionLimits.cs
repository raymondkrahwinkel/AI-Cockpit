using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Sessions;

// AC-2026: why a start under a profile would be refused right now because it runs its maximum of parallel sessions.
public interface IProfileSessionLimits
{
    // Null when a start would be admitted.
    string? FullReason(SessionProfile profile);
}
