using Cockpit.Core.Abstractions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// The generic host-side login starter — `ProfileLoginChecker`'s sibling: dispatches a profile's `StartLogin` to
// its provider plugin so the core carries no knowledge of any provider's auth mechanism. Same TTY-then-session
// precedence as `ProfileLoginChecker` (AC-629), for the same reason: an SDK-only provider can register `StartLogin`
// on its `SessionProviderRegistration` alone.
internal sealed class ProfileLoginStarter(
    IPluginTtyProviderRegistry ttyProviderRegistry,
    IPluginProviderRegistry? sessionProviderRegistry = null)
    : IProfileLoginStarter, ISingletonService
{
    public ILoginFlow? StartLogin(SessionProfile profile, CancellationToken cancellationToken)
    {
        if (profile.ProviderConfig is not PluginProviderConfig plugin)
        {
            // A profile-less/local session has no provider to start a login flow with.
            return null;
        }

        var startLogin = ttyProviderRegistry.Resolve(plugin.ProviderId)?.StartLogin
            ?? sessionProviderRegistry?.Resolve(plugin.ProviderId)?.StartLogin;

        return startLogin?.Invoke(plugin.ConfigJson, cancellationToken);
    }
}
