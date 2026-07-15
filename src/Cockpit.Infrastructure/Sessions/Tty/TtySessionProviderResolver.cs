using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// Default <see cref="ITtySessionProviderResolver"/>: a plugin's own TTY provider for a plugin profile that
/// registered one (Claude and Codex both do), the bundled Claude provider plugin for a profile-less session (which
/// runs the host's own CLI), and nothing for a provider that has no TUI to run.
/// </summary>
/// <remarks>
/// Fase 4: Claude is a provider plugin like every other — a Claude profile is migrated to a
/// <see cref="PluginProviderConfig"/> on load, so it resolves through the plugin arm.
/// </remarks>
internal sealed class TtySessionProviderResolver(
    IServiceProvider services,
    IPluginTtyProviderRegistry ttyProviderRegistry) : ITtySessionProviderResolver, ISingletonService
{
    public ITtySessionProvider? Resolve(SessionProfile? profile) => profile?.ProviderConfig switch
    {
        // A profile-less session runs the bundled Claude provider plugin's TUI with a default config.
        null => _ResolvePlugin(ClaudePluginProfile.ProviderId, "{}"),
        PluginProviderConfig plugin => _ResolvePlugin(plugin.ProviderId, plugin.ConfigJson),

        // A local HTTP model is not a program you can run in a terminal. Saying so is the point: the alternative
        // is a TTY option that starts something the operator did not choose.
        _ => null,
    };

    private ITtySessionProvider? _ResolvePlugin(string providerId, string configJson) =>
        ttyProviderRegistry.Resolve(providerId) is { } registration
            ? new PluginTtySessionProviderAdapter(
                registration.ProviderId,
                registration.CreateProvider(services),
                configJson,
                services.GetService<IMcpServerStore>())
            : null;
}
