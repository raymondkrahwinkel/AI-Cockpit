using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// Default <see cref="ITtySessionProviderResolver"/>: Claude's own TTY provider for a Claude profile (and for a
/// profile-less session, which runs the host's own CLI), a plugin's for a plugin profile that registered one,
/// and nothing for a provider that has no TUI to run.
/// </summary>
/// <remarks>
/// Fase 4: a Claude profile prefers the Claude provider <em>plugin</em> when it is installed, falling back to the
/// in-tree <see cref="ClaudeTtySessionProvider"/> during the transition — so moving Claude out to a plugin never
/// leaves a Claude session unable to start.
/// </remarks>
internal sealed class TtySessionProviderResolver(
    IServiceProvider services,
    IPluginTtyProviderRegistry ttyProviderRegistry) : ITtySessionProviderResolver, ISingletonService
{
    public ITtySessionProvider? Resolve(SessionProfile? profile) => profile?.ProviderConfig switch
    {
        null or ClaudeConfig => _ResolveClaude(profile?.Claude),
        PluginProviderConfig plugin => _ResolvePlugin(plugin.ProviderId, plugin.ConfigJson),

        // A local HTTP model is not a program you can run in a terminal. Saying so is the point: the alternative
        // is a TTY option that starts something the operator did not choose.
        _ => null,
    };

    private ITtySessionProvider? _ResolveClaude(ClaudeConfig? claude)
    {
        if (ttyProviderRegistry.Resolve(ClaudeTtySessionProvider.Id) is { } registration)
        {
            // The plugin reads the same two fields (config dir, executable path) from the profile's config JSON that
            // the in-tree provider read straight off ClaudeConfig.
            return _BuildPluginAdapter(registration, _SerializeClaudeConfig(claude));
        }

        return services.GetRequiredService<ClaudeTtySessionProvider>();
    }

    private ITtySessionProvider? _ResolvePlugin(string providerId, string configJson) =>
        ttyProviderRegistry.Resolve(providerId) is { } registration
            ? _BuildPluginAdapter(registration, configJson)
            : null;

    private ITtySessionProvider _BuildPluginAdapter(TtyProviderRegistration registration, string configJson) =>
        new PluginTtySessionProviderAdapter(
            registration.ProviderId,
            registration.CreateProvider(services),
            configJson,
            services.GetService<IMcpServerStore>());

    private static string _SerializeClaudeConfig(ClaudeConfig? claude) =>
        JsonSerializer.Serialize(new { configDir = claude?.ConfigDir, executablePath = claude?.ExecutablePath });
}
