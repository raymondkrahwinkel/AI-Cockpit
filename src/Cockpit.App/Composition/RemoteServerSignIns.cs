using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Composition;

internal sealed class RemoteServerSignIns(IRemoteServers servers) : IServerSignIns, ISingletonService
{
    public ILoginFlow? Start(string server, string profile, CancellationToken cancellationToken) =>
        servers.Servers.FirstOrDefault(candidate => candidate.Name == server) is IRemoteServerSignIn signIn
            ? signIn.StartSignIn(profile, cancellationToken)
            : null;
}
