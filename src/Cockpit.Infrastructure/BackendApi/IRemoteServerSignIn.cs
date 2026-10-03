using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.BackendApi;

/// <summary>
/// Signs a provider profile in on a connect server, through the sign-in routes only an admin key may use (AC-1357).
/// </summary>
public interface IRemoteServerSignIn
{
    /// <summary>
    /// Starts the profile's sign-in on the server; its steps are the provider's own. The server refuses a key that is not admin.
    /// </summary>
    ILoginFlow StartSignIn(string profile, CancellationToken cancellationToken);
}
