using Cockpit.App.Services;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Composition;

internal sealed class RemoteSessionLoginFlows(RemoteBackend backend, BackendApiClient client) : ISessionLoginFlows
{
    public bool CanStartLogin(SessionProfile profile) => backend.CanSignIn(profile.Label);

    public ILoginFlow? StartLogin(SessionProfile profile, CancellationToken cancellationToken) =>
        CanStartLogin(profile) ? new RemoteLoginFlow(client, profile.Label, cancellationToken) : null;
}
