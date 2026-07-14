using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// Default <see cref="ITtySessionProviderResolver"/>: Claude's own TTY provider for a Claude profile (and for a
/// profile-less session, which runs the host's own CLI), a plugin's for a plugin profile that registered one,
/// and nothing for a provider that has no TUI to run.
/// </summary>
internal sealed class TtySessionProviderResolver(
    IServiceProvider services,
    IPluginTtyProviderRegistry ttyProviderRegistry) : ITtySessionProviderResolver, ISingletonService
{
    public ITtySessionProvider? Resolve(SessionProfile? profile) => profile?.ProviderConfig switch
    {
        null or ClaudeConfig => services.GetRequiredService<ClaudeTtySessionProvider>(),
        PluginProviderConfig plugin => _ResolvePlugin(plugin),

        // A local HTTP model is not a program you can run in a terminal. Saying so is the point: the alternative
        // is a TTY option that starts something the operator did not choose.
        _ => null,
    };

    private ITtySessionProvider? _ResolvePlugin(PluginProviderConfig plugin)
    {
        if (ttyProviderRegistry.Resolve(plugin.ProviderId) is not { } registration)
        {
            return null;
        }

        return new PluginTtySessionProviderAdapter(
            registration.ProviderId,
            registration.CreateProvider(services),
            plugin.ConfigJson);
    }
}
