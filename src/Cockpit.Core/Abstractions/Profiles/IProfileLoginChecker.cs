using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Profiles;

/// <summary>
/// Checks whether a <see cref="SessionProfile"/> is logged in, generically: the host gates a session start and
/// shows the login prompt without knowing what "logged in" means for any provider. Dispatches to the profile's
/// provider plugin, which answers from its own config (existence-only by contract — never reading a credential's
/// contents, Iron Law #8). A provider with no login concept, or a profile whose provider declares none, is
/// treated as always ready.
/// </summary>
public interface IProfileLoginChecker
{
    /// <summary>True when the profile's provider reports it logged in; true for a provider that has no login gate, false when its gate reports logged out.</summary>
    bool IsLoggedIn(SessionProfile profile);
}
