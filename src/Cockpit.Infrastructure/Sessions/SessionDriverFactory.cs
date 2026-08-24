using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

// `ISessionDriverFactory` resolves a fresh driver per session from the container — the sanctioned use of
// `IServiceProvider` (Code.md §2), building a runtime-parameterized child chosen by the profile's provider.
// AC-1013: local (Ollama/LmStudio) uses the in-tree OpenAI-compat driver; everything else, incl. Claude (Fase 4, migrated to `PluginProviderConfig` on load), runs a plugin-registered driver wrapped in `PluginSessionDriverAdapter`.
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

        // MCP selection (#44) resolves against the shared registry host-side (plugin isolation) before reaching the
        // driver. GetService, not GetRequiredService: the store is always registered in the running app; its absence
        // (a unit test wiring only the registry) means no fan-out — same reasoning for the conversation sink (AC-408).
        return new PluginSessionDriverAdapter(driver, registration.Capabilities, services.GetRequiredService<Mcp.McpAuthKey>(), services.GetService<IMcpServerCatalog>(), services.GetService<ILogger<PluginSessionDriverAdapter>>(), services.GetService<Mcp.SessionMcpKeyring>(), sessionResources: null, oauthCoordinator: services.GetService<IMcpOAuthCoordinator>(), conversationSink: services.GetService<Core.Sessions.ISessionConversationSink>(), oauthProxy: services.GetService<IMcpOAuthProxy>(), worktreeManager: services.GetService<Core.Abstractions.Worktrees.IWorktreeManager>(), mcpMounts: services.GetService<Core.Sessions.SessionMcpMounts>(), mcpToolProvider: services.GetService<Mcp.IMcpToolProvider>());
    }

    // A provider going missing is almost never "no such provider" — it is a plugin that did not load: disabled,
    // awaiting re-approval after an update changed its bytes (consent pin mismatch), or built against a different
    // contract. The raw "not registered" error reads like a bug in the app; this says where to look instead.
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
