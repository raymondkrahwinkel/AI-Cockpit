using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// <see cref="ISessionDriverFactory"/> that resolves a fresh driver per session from the container. It is
/// an orchestrator building a runtime-parameterized child (the driver chosen by the profile's provider),
/// which is the sanctioned use of <see cref="IServiceProvider"/> (Code.md §2) — both built-in drivers are
/// transient, so each call yields a new instance for the new session. A <see cref="SessionProvider.Plugin"/>
/// profile grows one more arm (#45): the registered plugin's own driver factory is resolved from
/// <see cref="IPluginProviderRegistry"/> and wrapped in a <see cref="PluginSessionDriverAdapter"/>.
/// </summary>
/// <remarks>
/// Fase 4: a Claude profile (and a profile-less session) prefers the Claude provider <em>plugin</em>'s SDK route when
/// it is installed, falling back to the in-tree <see cref="ClaudeCliSession"/> during the transition — the
/// session-driver mirror of <see cref="Tty.TtySessionProviderResolver"/>'s Claude preference, so moving Claude out to
/// a plugin never leaves a Claude session unable to start.
/// </remarks>
internal sealed class SessionDriverFactory(IServiceProvider services, IPluginProviderRegistry pluginProviderRegistry) : ISessionDriverFactory, ISingletonService
{
    public ISessionDriver Create(SessionProfile? profile)
    {
        if (profile is null)
        {
            return _ResolveClaude(claude: null);
        }

        return profile.Provider switch
        {
            SessionProvider.Ollama or SessionProvider.LmStudio => services.GetRequiredService<OpenAiCompatSessionDriver>(),
            SessionProvider.Plugin => _CreatePluginDriver(profile),
            _ => _ResolveClaude(profile.Claude),
        };
    }

    private ISessionDriver _ResolveClaude(ClaudeConfig? claude)
    {
        // Prefer the Claude provider plugin's SDK route when installed (its permissions ride the control protocol, no
        // HTTP MCP server), falling back to the in-tree driver otherwise. The plugin reads the same two fields (config
        // dir, executable path) from the profile's config JSON that the in-tree driver read straight off ClaudeConfig.
        if (pluginProviderRegistry.Resolve(Tty.ClaudeTtySessionProvider.Id) is { } registration)
        {
            var driver = registration.CreateDriverFactory(services).Create(_SerializeClaudeConfig(claude));
            return new PluginSessionDriverAdapter(driver, registration.Capabilities, services.GetService<IMcpServerStore>());
        }

        return services.GetRequiredService<ClaudeCliSession>();
    }

    private static string _SerializeClaudeConfig(ClaudeConfig? claude) =>
        JsonSerializer.Serialize(new { configDir = claude?.ConfigDir, executablePath = claude?.ExecutablePath });

    private ISessionDriver _CreatePluginDriver(SessionProfile profile)
    {
        if (profile.ProviderConfig is not PluginProviderConfig pluginConfig)
        {
            throw new InvalidOperationException($"A {nameof(SessionProvider.Plugin)} profile must carry a {nameof(PluginProviderConfig)}.");
        }

        var registration = pluginProviderRegistry.Resolve(pluginConfig.ProviderId)
            ?? throw new InvalidOperationException($"No plugin session provider is registered for '{pluginConfig.ProviderId}'.");

        var driver = registration.CreateDriverFactory(services).Create(pluginConfig.ConfigJson);

        // The adapter resolves the operator's per-session MCP selection (#44) against the shared registry before
        // handing the endpoints to the plugin driver — the registry stays host-side (plugin isolation). GetService,
        // not GetRequiredService: the store is always registered in the running app, and its absence (a unit test
        // that wires only the registry) simply means no fan-out, which the adapter already handles.
        return new PluginSessionDriverAdapter(driver, registration.Capabilities, services.GetService<IMcpServerStore>());
    }
}
