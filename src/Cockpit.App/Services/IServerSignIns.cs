using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

/// <summary>
/// Starts a provider sign-in on a connect server (AC-1457), through the server's own sign-in routes.
/// </summary>
public interface IServerSignIns
{
    /// <summary>
    /// The sign-in of <paramref name="profile"/> on <paramref name="server"/>; null when that server is not connected.
    /// </summary>
    ILoginFlow? Start(string server, string profile, CancellationToken cancellationToken);
}
