using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// A server's own GitHub sign-in for its clones (AC-1987), as Options › Projects on that server shows it.
/// </summary>
public interface IServerGitHubCredentials
{
    Task<ServerGitHubStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    ILoginFlow StartSignIn(bool useToken, CancellationToken cancellationToken);

    Task SignOutAsync(CancellationToken cancellationToken = default);
}

public sealed record ServerGitHubStatus(bool Available, bool SignedIn, string? User, string Source, string Message);
