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

    /// <summary>
    /// Awaits a current sign-in reading when the provider supports one; otherwise returns <see cref="IsLoggedIn"/>.
    /// </summary>
    Task<bool> CheckAsync(SessionProfile profile, CancellationToken cancellationToken) => Task.FromResult(IsLoggedIn(profile));

    /// <summary>
    /// True when the profile's provider declares a sign-in check, so <see cref="IsLoggedIn"/> asked something.
    /// </summary>
    bool HasLoginCheck(SessionProfile profile) => false;

    /// <summary>
    /// What the profile's provider says its sign-in rests on; <see cref="ProfileCredentialKind.Unknown"/> when it cannot say.
    /// </summary>
    ProfileCredentialKind CredentialKind(SessionProfile profile) => ProfileCredentialKind.Unknown;
}
