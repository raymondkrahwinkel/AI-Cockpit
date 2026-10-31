using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.BackendApi;

public interface IRemoteGitHubCredentials
{
    Task<RemoteGitHubStatus> GetGitHubStatusAsync(CancellationToken cancellationToken = default);

    ILoginFlow StartGitHubSignIn(bool useToken, CancellationToken cancellationToken);

    Task SignOutGitHubAsync(CancellationToken cancellationToken = default);
}

public sealed record RemoteGitHubStatus(bool Available, bool SignedIn, string? User, string Source, string Message);
