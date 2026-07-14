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

    public string? SystemPrompt { get; set; }

    /// <summary>The registered provider's id, for a plugin-backed profile (#45) — see <see cref="PluginProviderConfig"/>.</summary>
    public string? PluginProviderId { get; set; }

    /// <summary>The plugin's own config record, serialized as JSON, for a plugin-backed profile (#45).</summary>
    public string? PluginConfigJson { get; set; }

    /// <summary>
    /// Maps a domain config to its on-disk form. A Claude profile writes a block too — one that says only which
    /// provider it is, since its settings live in the entry's own <c>ConfigDir</c>/<c>ExecutablePath</c> fields.
    /// It used to write nothing at all, and absence meant Claude: a config in which the most-used provider was
    /// the one you could not see.
    /// </summary>
    public static ProviderConfigEntry FromDomain(ProviderConfig config) => config switch
    {
        ClaudeConfig => new() { Provider = SessionProvider.ClaudeCli },
        OllamaConfig ollama => new() { Provider = SessionProvider.Ollama, BaseUrl = ollama.BaseUrl, Model = ollama.Model, SystemPrompt = ollama.SystemPrompt },
        LmStudioConfig lmStudio => new() { Provider = SessionProvider.LmStudio, BaseUrl = lmStudio.BaseUrl, Model = lmStudio.Model, ApiKey = lmStudio.ApiKey, SystemPrompt = lmStudio.SystemPrompt },
        PluginProviderConfig plugin => new() { Provider = SessionProvider.Plugin, PluginProviderId = plugin.ProviderId, PluginConfigJson = plugin.ConfigJson },
        _ => throw new InvalidOperationException($"No on-disk shape is defined for provider config {config.GetType().Name}."),
    };

    /// <summary>
    /// Maps the on-disk block back to a domain config. Claude's settings do not live in this block, so they are
    /// passed in from the entry that owns them.
    /// </summary>
    public ProviderConfig ToDomain(string claudeConfigDir, string? claudeExecutablePath) => Provider switch
    {
        SessionProvider.Ollama => new OllamaConfig(BaseUrl ?? string.Empty, Model ?? string.Empty, SystemPrompt),
        SessionProvider.LmStudio => new LmStudioConfig(BaseUrl ?? string.Empty, Model ?? string.Empty, ApiKey, SystemPrompt),
        SessionProvider.Plugin => new PluginProviderConfig(PluginProviderId ?? string.Empty, PluginConfigJson ?? string.Empty),
        _ => new ClaudeConfig(claudeConfigDir, claudeExecutablePath),
    };
}
