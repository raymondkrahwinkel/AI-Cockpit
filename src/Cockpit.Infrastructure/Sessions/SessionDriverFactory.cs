using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// <see cref="ISessionDriverFactory"/> that resolves a fresh driver per session from the container. It is
/// an orchestrator building a runtime-parameterized child (the driver chosen by the profile's provider),
/// which is the sanctioned use of <see cref="IServiceProvider"/> (Code.md §2). A local provider is an in-tree
/// OpenAI-compatible driver; every other session — a plugin profile, and a profile-less default session — runs
/// a plugin-registered driver resolved from <see cref="IPluginProviderRegistry"/> and wrapped in a
/// <see cref="PluginSessionDriverAdapter"/>.
/// </summary>
/// <remarks>
/// Fase 4: Claude is a provider plugin like every other. A Claude profile is migrated to a
/// <see cref="PluginProviderConfig"/> on load, so it takes the plugin arm; a profile-less default session runs the
/// bundled Claude provider plugin with a default config.
/// </remarks>
internal sealed class SessionDriverFactory(IServiceProvider services, IPluginProviderRegistry pluginProviderRegistry) : ISessionDriverFactory, ISingletonService
{
    public ISessionDriver Create(SessionProfile? profile)
    {
        if (profile is null)
        {
            return _CreatePluginDriver(ClaudePluginProfile.ProviderId, configJson: "{}");
        }

        return profile.Provider switch
        {
            SessionProvider.Ollama or SessionProvider.LmStudio => services.GetRequiredService<OpenAiCompatSessionDriver>(),
            SessionProvider.Plugin when profile.ProviderConfig is PluginProviderConfig pluginConfig => _CreatePluginDriver(pluginConfig.ProviderId, pluginConfig.ConfigJson),
            SessionProvider.Plugin => throw new InvalidOperationException($"A {nameof(SessionProvider.Plugin)} profile must carry a {nameof(PluginProviderConfig)}."),
            _ => _CreatePluginDriver(ClaudePluginProfile.ProviderId, configJson: "{}"),
        };
    }

    private ISessionDriver _CreatePluginDriver(string providerId, string configJson)
    {
        var registration = pluginProviderRegistry.Resolve(providerId)
            ?? throw new InvalidOperationException($"No plugin session provider is registered for '{providerId}'.");

        var driver = registration.CreateDriverFactory(services).Create(configJson);

        // The adapter resolves the operator's per-session MCP selection (#44) against the shared registry before
        // handing the endpoints to the plugin driver — the registry stays host-side (plugin isolation). GetService,
        // not GetRequiredService: the store is always registered in the running app, and its absence (a unit test
        // that wires only the registry) simply means no fan-out, which the adapter already handles.
        return new PluginSessionDriverAdapter(driver, registration.Capabilities, services.GetService<IMcpServerCatalog>(), services.GetService<ILogger<PluginSessionDriverAdapter>>());
    }
}
