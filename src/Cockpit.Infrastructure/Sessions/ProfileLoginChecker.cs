using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Infrastructure.Sessions;

// The generic host-side login gate (Fase 4): dispatches a profile's login check to its provider plugin's
// `IsLoggedIn` delegate; no gate means always ready. AC-629: both `TtyProviderRegistration` and the session
// registry are consulted (TTY wins ties), so an SDK-only provider can't fall through and read as falsely ready.
internal sealed class ProfileLoginChecker(
    IPluginTtyProviderRegistry ttyProviderRegistry,
    IPluginProviderRegistry? sessionProviderRegistry = null)
    : IProfileLoginChecker, ISingletonService
{
    public bool IsLoggedIn(SessionProfile profile)
    {
        if (profile.ProviderConfig is not PluginProviderConfig plugin)
        {
            // A profile-less/local session has no provider login gate to fail — it is ready to start.
            return true;
        }

        var isLoggedIn = ttyProviderRegistry.Resolve(plugin.ProviderId)?.IsLoggedIn
            ?? sessionProviderRegistry?.Resolve(plugin.ProviderId)?.IsLoggedIn;

        // No gate declared → nothing to be logged out of; the provider manages its own auth.
        return isLoggedIn is null || isLoggedIn(plugin.ConfigJson);
    }
}
