using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Starts an in-app login attempt for a <see cref="SessionProfile"/>, generically: dispatches to whichever
/// provider plugin registered a <c>StartLogin</c> delegate — the same lookup <c>ProfileLoginChecker</c> does for
/// <c>IsLoggedIn</c>. Public (not internal), mirroring <see cref="IPluginProviderRegistry"/>, so the profile-editor
/// and transcript-row view models can resolve it from the container directly.
/// </summary>
public interface IProfileLoginStarter
{
    /// <summary>Starts the flow, or <see langword="null"/> when this profile's provider offers no in-app login.</summary>
    ILoginFlow? StartLogin(SessionProfile profile, CancellationToken cancellationToken);
}
