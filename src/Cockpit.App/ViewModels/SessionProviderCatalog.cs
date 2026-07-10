using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>The providers offered when creating a profile (#26), with their display labels and the default local server URLs.</summary>
public static class SessionProviderCatalog
{
    public static IReadOnlyList<SessionProviderOption> Providers { get; } =
    [
        new("Claude CLI", SessionProvider.ClaudeCli),
        new("Ollama", SessionProvider.Ollama),
        new("LM Studio", SessionProvider.LmStudio),
    ];

    public static SessionProviderOption Resolve(SessionProvider provider) =>
        Providers.FirstOrDefault(option => option.Value == provider) ?? Providers[0];

    /// <summary>The default base URL for a local provider's OpenAI-compatible server, pre-filled when the provider is picked.</summary>
    public static string DefaultBaseUrl(SessionProvider provider) => provider switch
    {
        SessionProvider.Ollama => "http://localhost:11434",
        SessionProvider.LmStudio => "http://localhost:1234",
        _ => string.Empty,
    };
}
