using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;

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

        var isLoggedIn = _Gate(plugin);

        // No gate declared → nothing to be logged out of; the provider manages its own auth.
        return isLoggedIn is null || isLoggedIn(plugin.ConfigJson);
    }

    public Task<bool> CheckAsync(SessionProfile profile, CancellationToken cancellationToken)
    {
        if (profile.ProviderConfig is not PluginProviderConfig plugin)
        {
            return Task.FromResult(true);
        }

        var check = _AsyncGate(plugin);
        return check is null ? Task.FromResult(IsLoggedIn(profile)) : check(plugin.ConfigJson, cancellationToken);
    }

    public bool HasLoginCheck(SessionProfile profile) => profile.ProviderConfig is PluginProviderConfig plugin && _Gate(plugin) is not null;

    public ProfileCredentialKind CredentialKind(SessionProfile profile)
    {
        if (profile.ProviderConfig is not PluginProviderConfig plugin)
        {
            return ProfileCredentialKind.Unknown;
        }

        var kind = ttyProviderRegistry.Resolve(plugin.ProviderId)?.CredentialKind
            ?? sessionProviderRegistry?.Resolve(plugin.ProviderId)?.CredentialKind;
        return kind?.Invoke(plugin.ConfigJson) switch
        {
            PluginCredentialKind.RenewingLogin => ProfileCredentialKind.RenewingLogin,
            PluginCredentialKind.Login => ProfileCredentialKind.Login,
            PluginCredentialKind.ApiKey => ProfileCredentialKind.ApiKey,
            PluginCredentialKind.ApiKeyFromSecret => ProfileCredentialKind.ApiKeyFromSecret,
            _ => ProfileCredentialKind.Unknown,
        };
    }

    private Func<string, bool>? _Gate(PluginProviderConfig plugin) =>
        ttyProviderRegistry.Resolve(plugin.ProviderId)?.IsLoggedIn
        ?? sessionProviderRegistry?.Resolve(plugin.ProviderId)?.IsLoggedIn;

    private Func<string, CancellationToken, Task<bool>>? _AsyncGate(PluginProviderConfig plugin) =>
        ttyProviderRegistry.Resolve(plugin.ProviderId)?.CheckLoginAsync
        ?? sessionProviderRegistry?.Resolve(plugin.ProviderId)?.CheckLoginAsync;
}
