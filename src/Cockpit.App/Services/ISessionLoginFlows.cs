using Cockpit.Core.Abstractions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// Starts an in-app login for a session's profile (AC-1449). Local to the desktop: a remote frontend needs the flow
/// over the line first (AC-1388).
/// </summary>
public interface ISessionLoginFlows
{
    /// <summary>
    /// A login attempt for <paramref name="profile"/>; null when its provider has no in-app login.
    /// </summary>
    ILoginFlow? StartLogin(SessionProfile profile, CancellationToken cancellationToken);
}

internal sealed class ProfileLoginFlows(IProfileLoginStarter starter) : ISessionLoginFlows, ISingletonService
{
    public ILoginFlow? StartLogin(SessionProfile profile, CancellationToken cancellationToken) =>
        starter.StartLogin(profile, cancellationToken);
}
