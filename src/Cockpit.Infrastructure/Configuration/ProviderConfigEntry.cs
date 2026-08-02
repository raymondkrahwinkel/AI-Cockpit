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

    // The registered provider's id, for a plugin-backed profile (#45) — see `PluginProviderConfig`.
    public string? PluginProviderId { get; set; }

    // The plugin's own config record, serialized as JSON, for a plugin-backed profile (#45).
    public string? PluginConfigJson { get; set; }

    // Maps a domain config to its on-disk form. A Claude profile writes a block too — one that says only which
    // provider it is, since its settings live in the entry's own `ConfigDir`/`ExecutablePath` fields.
    // It used to write nothing at all, and absence meant Claude: a config in which the most-used provider was
    // the one you could not see.
    public static ProviderConfigEntry FromDomain(ProviderConfig config) => config switch
    {
        ClaudeConfig => new() { Provider = SessionProvider.ClaudeCli },
        OllamaConfig ollama => new() { Provider = SessionProvider.Ollama, BaseUrl = ollama.BaseUrl, Model = ollama.Model, SystemPrompt = ollama.SystemPrompt },
        LmStudioConfig lmStudio => new() { Provider = SessionProvider.LmStudio, BaseUrl = lmStudio.BaseUrl, Model = lmStudio.Model, ApiKey = lmStudio.ApiKey, SystemPrompt = lmStudio.SystemPrompt },
        PluginProviderConfig plugin => new() { Provider = SessionProvider.Plugin, PluginProviderId = plugin.ProviderId, PluginConfigJson = plugin.ConfigJson },
        _ => throw new InvalidOperationException($"No on-disk shape is defined for provider config {config.GetType().Name}."),
    };

    // Maps the on-disk block back to a domain config. A Claude entry (an explicit `SessionProvider.ClaudeCli`
    // or an older entry with no provider block at all) is migrated to the bundled Claude provider plugin on load, so its
    // settings — which still live at the top of the owning entry — become that plugin's config (Fase 4). Idempotent: a
    // profile already stored as a plugin comes back through the `SessionProvider.Plugin` arm unchanged.
    public ProviderConfig ToDomain(string claudeConfigDir, string? claudeExecutablePath) => Provider switch
    {
        SessionProvider.Ollama => new OllamaConfig(BaseUrl ?? string.Empty, Model ?? string.Empty, SystemPrompt),
        SessionProvider.LmStudio => new LmStudioConfig(BaseUrl ?? string.Empty, Model ?? string.Empty, ApiKey, SystemPrompt),
        SessionProvider.Plugin => new PluginProviderConfig(PluginProviderId ?? string.Empty, PluginConfigJson ?? string.Empty),
        _ => ClaudePluginProfile.Create(claudeConfigDir, claudeExecutablePath),
    };
}
