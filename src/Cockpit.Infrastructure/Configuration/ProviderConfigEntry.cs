using System.Text.Json;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a profile's `ProviderConfig` — a flat record discriminated on
// `Provider`, so the polymorphic domain config round-trips through plain JSON without
// System.Text.Json type-discriminator attributes leaking onto the domain records.
internal sealed class ProviderConfigEntry
{
    public SessionProvider Provider { get; set; }

    public string? BaseUrl { get; set; }

    public string? Model { get; set; }

    public string? ApiKey { get; set; }

    public string? SystemPrompt { get; set; }

    // AC-1489: an Ollama/LM Studio profile's optional context-guard bounds, carried as the JSON it was written as.
    public JsonElement? TurnLimits { get; set; }

    // The registered provider's id, for a plugin-backed profile (#45) — see `PluginProviderConfig`.
    public string? PluginProviderId { get; set; }

    // The plugin's own config record, serialized as JSON, for a plugin-backed profile (#45).
    public string? PluginConfigJson { get; set; }

    // Maps a domain config to its on-disk form. A Claude profile now writes a block too, saying only which
    // provider it is; it used to write nothing, so absence meant Claude — the most-used provider unseen.
    public static ProviderConfigEntry FromDomain(ProviderConfig config) => config switch
    {
        ClaudeConfig => new() { Provider = SessionProvider.ClaudeCli },
        OllamaConfig ollama => new() { Provider = SessionProvider.Ollama, BaseUrl = ollama.BaseUrl, Model = ollama.Model, SystemPrompt = ollama.SystemPrompt, TurnLimits = ollama.TurnLimits },
        LmStudioConfig lmStudio => new() { Provider = SessionProvider.LmStudio, BaseUrl = lmStudio.BaseUrl, Model = lmStudio.Model, ApiKey = lmStudio.ApiKey, SystemPrompt = lmStudio.SystemPrompt, TurnLimits = lmStudio.TurnLimits },
        PluginProviderConfig plugin => new() { Provider = SessionProvider.Plugin, PluginProviderId = plugin.ProviderId, PluginConfigJson = plugin.ConfigJson },
        _ => throw new InvalidOperationException($"No on-disk shape is defined for provider config {config.GetType().Name}."),
    };

    // Maps the on-disk block back to a domain config. A Claude entry (explicit or a legacy blockless one)
    // is migrated to the bundled Claude provider plugin on load (Fase 4); already-plugin entries pass through.
    public ProviderConfig ToDomain(string claudeConfigDir, string? claudeExecutablePath) => Provider switch
    {
        SessionProvider.Ollama => new OllamaConfig(BaseUrl ?? string.Empty, Model ?? string.Empty, SystemPrompt, TurnLimits),
        SessionProvider.LmStudio => new LmStudioConfig(BaseUrl ?? string.Empty, Model ?? string.Empty, ApiKey, SystemPrompt, TurnLimits),
        SessionProvider.Plugin => new PluginProviderConfig(PluginProviderId ?? string.Empty, PluginConfigJson ?? string.Empty),
        _ => ClaudePluginProfile.Create(claudeConfigDir, claudeExecutablePath),
    };
}
