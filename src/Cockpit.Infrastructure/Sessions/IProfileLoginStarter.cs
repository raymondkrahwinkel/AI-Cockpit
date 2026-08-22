using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Starts an in-app login attempt for a <see cref="SessionProfile"/>, dispatched to whichever provider plugin
/// registered a <c>StartLogin</c> delegate. Public (not internal), mirroring <see cref="IPluginProviderRegistry"/>, so view models can resolve it from the container directly.
/// </summary>
public interface IProfileLoginStarter
{
    /// <summary>
    /// Whether this profile's provider declared a <c>StartLogin</c> at all, with no subprocess spawned.
    /// </summary>
    bool CanStartLogin(SessionProfile profile);

    /// <summary>
    /// Starts the flow, or <see langword="null"/> when this profile's provider offers no in-app login.
    /// </summary>
    ILoginFlow? StartLogin(SessionProfile profile, CancellationToken cancellationToken);
}
