using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.ViewModels;

// The providers offered when creating a profile (#26), with their display labels and the default local server URLs.
public static class SessionProviderCatalog
{
    public static IReadOnlyList<SessionProviderOption> Providers { get; } =
    [
        // Only the two OpenAI-compatible local providers remain built into the core (Fase 4): Claude is a bundled
        // provider plugin now, offered through the plugin-provider arm, not a built-in CLI provider.
        new("Ollama", SessionProvider.Ollama),
        new("LM Studio", SessionProvider.LmStudio),
        // Generic fallback label for a Plugin-provider profile shown somewhere that has no IPluginProviderRegistry
        // at hand (and so can't look up the specific plugin's own display name) — never shown in the profile
        // editor's own dropdown, which uses AllProviders below instead.
        new("Plugin", SessionProvider.Plugin),
    ];

    // A provider with no built-in option (the retired Claude-CLI enum value) falls back to Ollama, the first core
    // provider — reachable only for a legacy value, since a Claude profile is migrated to the plugin on load.
    public static SessionProviderOption Resolve(SessionProvider provider) =>
        Providers.FirstOrDefault(option => option.Value == provider) ?? Providers[0];

    // The full provider picker for the profile editor (#45): the built-in providers plus one option per provider a
    // Keep each registered plugin provider distinct instead of collapsing them onto the generic placeholder (#45).
    public static IReadOnlyList<SessionProviderOption> AllProviders(IPluginProviderRegistry pluginProviderRegistry) =>
    [
        .. Providers.Where(option => option.Value != SessionProvider.Plugin),
        .. pluginProviderRegistry.Registrations.Select(registration =>
            new SessionProviderOption(registration.DisplayName, SessionProvider.Plugin, registration.ProviderId)),
    ];

    // The default base URL for a local provider's OpenAI-compatible server, pre-filled when the provider is picked.
    public static string DefaultBaseUrl(SessionProvider provider) => provider switch
    {
        SessionProvider.Ollama => "http://localhost:11434",
        SessionProvider.LmStudio => "http://localhost:1234",
        _ => string.Empty,
    };
}
