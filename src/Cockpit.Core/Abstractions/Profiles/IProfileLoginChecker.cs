using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Profiles;

/// <summary>
/// Checks whether a <see cref="SessionProfile"/> is logged in, generically: the host gates a session start without
/// knowing what "logged in" means per provider. Dispatches to the profile's provider plugin, which answers from
/// its own config (existence-only by contract, Iron Law #8). No login concept, or none declared, means always ready.
/// </summary>
public interface IProfileLoginChecker
{
    /// <summary>
    /// True when the profile's provider reports it logged in; true for a provider that has no login gate, false when its gate reports logged out.
    /// </summary>
    bool IsLoggedIn(SessionProfile profile);
}
