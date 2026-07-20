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
            ?? throw new InvalidOperationException(_ProviderNotRegisteredMessage(providerId));

        var driver = registration.CreateDriverFactory(services).Create(configJson);

        // The adapter resolves the operator's per-session MCP selection (#44) against the shared registry before
        // handing the endpoints to the plugin driver — the registry stays host-side (plugin isolation). GetService,
        // not GetRequiredService: the store is always registered in the running app, and its absence (a unit test
        // that wires only the registry) simply means no fan-out, which the adapter already handles.
        return new PluginSessionDriverAdapter(driver, registration.Capabilities, services.GetRequiredService<Mcp.McpAuthKey>(), services.GetService<IMcpServerCatalog>(), services.GetService<ILogger<PluginSessionDriverAdapter>>(), services.GetService<Mcp.SessionMcpKeyring>());
    }

    // A provider going missing is almost never "no such provider" — it is a provider plugin that did not load:
    // disabled, or awaiting re-approval after an update changed its bytes (its consent pin no longer matches), or
    // built against a different contract. The raw "not registered for 'claude'" reads like a bug in the app; this
    // says where to look and what is actually available instead, so a vanished provider is a pointer, not a wall.
    private string _ProviderNotRegisteredMessage(string providerId)
    {
        var available = pluginProviderRegistry.Registrations
            .Select(registration => registration.ProviderId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var availableClause = available.Count == 0
            ? "No session providers are loaded at all."
            : $"Available providers: {string.Join(", ", available)}.";

        return $"The '{providerId}' provider is not available — its plugin is installed but did not load "
            + $"(it may be disabled, awaiting approval after an update, or built for a different contract version). "
            + $"Open Plugin Manager to check its status. {availableClause}";
    }
}
