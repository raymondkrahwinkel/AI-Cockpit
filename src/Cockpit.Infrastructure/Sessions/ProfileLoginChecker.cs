using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Infrastructure.Sessions;

// The generic host-side login gate (Fase 4): dispatches a profile's login check to its provider plugin —
// whichever registered an `IsLoggedIn` delegate — so the core carries no knowledge of any provider's credential
// file. A profile whose provider declares no gate (a local model, or a plugin that self-manages auth) is treated
// as always ready, so it is never falsely reported logged out.
//
// Both registries are consulted (AC-629). The gate started on `TtyProviderRegistration` alone, which a provider
// registering *only* a session provider could not fill: Gemini, GitHub Models and Kimi resolved to nothing and
// every profile under them read as ready. Claude only escaped that by registering both of its routes under one
// id. The TTY registration keeps first say so a provider that fills both declares the pair once and gets the
// same answer on either route.
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
