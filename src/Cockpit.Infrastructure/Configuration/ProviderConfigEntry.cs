using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of a profile's <see cref="ProviderConfig"/> — a flat record discriminated on
/// <see cref="Provider"/>, so the polymorphic domain config round-trips through plain JSON without
/// System.Text.Json type-discriminator attributes leaking onto the domain records.
/// </summary>
internal sealed class ProviderConfigEntry
{
    public SessionProvider Provider { get; set; }

    public string? BaseUrl { get; set; }

    public string? Model { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Maps a domain config to its on-disk form. Returns <see langword="null"/> for the Claude-CLI provider, which carries no config record (the profile's own fields hold its settings).</summary>
    public static ProviderConfigEntry? FromDomain(ProviderConfig? config) => config switch
    {
        OllamaConfig ollama => new() { Provider = SessionProvider.Ollama, BaseUrl = ollama.BaseUrl, Model = ollama.Model },
        LmStudioConfig lmStudio => new() { Provider = SessionProvider.LmStudio, BaseUrl = lmStudio.BaseUrl, Model = lmStudio.Model, ApiKey = lmStudio.ApiKey },
        _ => null,
    };

    public ProviderConfig? ToDomain() => Provider switch
    {
        SessionProvider.Ollama => new OllamaConfig(BaseUrl ?? string.Empty, Model ?? string.Empty),
        SessionProvider.LmStudio => new LmStudioConfig(BaseUrl ?? string.Empty, Model ?? string.Empty, ApiKey),
        _ => null,
    };
}
