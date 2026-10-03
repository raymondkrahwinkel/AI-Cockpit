using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Composition;

internal sealed class ProfileLoginFlows(IProfileLoginStarter starter) : ISessionLoginFlows, ISingletonService
{
    public bool CanStartLogin(SessionProfile profile) => starter.CanStartLogin(profile);

    public ILoginFlow? StartLogin(SessionProfile profile, CancellationToken cancellationToken) =>
        starter.StartLogin(profile, cancellationToken);
}
