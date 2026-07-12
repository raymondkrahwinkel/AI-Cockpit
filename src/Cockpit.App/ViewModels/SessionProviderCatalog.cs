using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>The providers offered when creating a profile (#26), with their display labels and the default local server URLs.</summary>
public static class SessionProviderCatalog
{
    public static IReadOnlyList<SessionProviderOption> Providers { get; } =
    [
        new("Claude CLI", SessionProvider.ClaudeCli),
        new("Ollama", SessionProvider.Ollama),
        new("LM Studio", SessionProvider.LmStudio),
        // Generic fallback label for a Plugin-provider profile shown somewhere that has no IPluginProviderRegistry
        // at hand (and so can't look up the specific plugin's own display name) — never shown in the profile
        // editor's own dropdown, which uses AllProviders below instead.
        new("Plugin", SessionProvider.Plugin),
    ];

    public static SessionProviderOption Resolve(SessionProvider provider) =>
        Providers.FirstOrDefault(option => option.Value == provider) ?? Providers[0];

    /// <summary>
    /// The full provider picker for the profile editor (#45): the built-in providers plus one option per
    /// provider a plugin has registered, each carrying its own <see cref="SessionProviderOption.PluginProviderId"/>
    /// so several plugin-registered providers are individually selectable rather than collapsing onto the
    /// generic <see cref="Providers"/> "Plugin" placeholder.
    /// </summary>
    public static IReadOnlyList<SessionProviderOption> AllProviders(IPluginProviderRegistry pluginProviderRegistry) =>
    [
        .. Providers.Where(option => option.Value != SessionProvider.Plugin),
        .. pluginProviderRegistry.Registrations.Select(registration =>
            new SessionProviderOption(registration.DisplayName, SessionProvider.Plugin, registration.ProviderId)),
    ];

    /// <summary>The default base URL for a local provider's OpenAI-compatible server, pre-filled when the provider is picked.</summary>
    public static string DefaultBaseUrl(SessionProvider provider) => provider switch
    {
        SessionProvider.Ollama => "http://localhost:11434",
        SessionProvider.LmStudio => "http://localhost:1234",
        _ => string.Empty,
    };
}
