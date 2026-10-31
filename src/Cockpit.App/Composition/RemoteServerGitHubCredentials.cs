using Cockpit.App.Services;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Composition;

// AC-1987: the server connection's GitHub routes behind the frontend's own contract, so a view model never names
// the backend client (AC-1441).
internal sealed class RemoteServerGitHubCredentials(IRemoteGitHubCredentials server) : IServerGitHubCredentials
{
    public static IServerGitHubCredentials? For(object server) =>
        server is IRemoteGitHubCredentials remote ? new RemoteServerGitHubCredentials(remote) : null;

    public async Task<ServerGitHubStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await server.GetGitHubStatusAsync(cancellationToken).ConfigureAwait(false);
        return new ServerGitHubStatus(status.Available, status.SignedIn, status.User, status.Source, status.Message);
    }

    public ILoginFlow StartSignIn(bool useToken, CancellationToken cancellationToken) => server.StartGitHubSignIn(useToken, cancellationToken);

    public Task SignOutAsync(CancellationToken cancellationToken = default) => server.SignOutGitHubAsync(cancellationToken);
}
