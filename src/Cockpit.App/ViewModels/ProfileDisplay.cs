using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Formats a profile's label for the profile list and pickers with its provider — and the model for a
/// local provider (#26): e.g. <c>default (Claude CLI)</c> or <c>local (LM Studio - qwen2.5)</c>, so it is
/// clear at a glance which backend (and which local model) a profile runs.
/// </summary>
public static class ProfileDisplay
{
    /// <param name="pluginProviderName">
    /// The specific plugin provider's own display name (e.g. "Claude") for a Plugin-provider profile, resolved by
    /// the caller from the provider registry — <see cref="ProfileDisplay"/> has no registry access to look up a
    /// plugin's label from the bare <see cref="SessionProvider.Plugin"/> enum value. When null, the generic
    /// provider label is used (the "Plugin" placeholder for a plugin profile), preserving the pre-registry behaviour.
    /// </param>
    public static string Format(string label, SessionProvider provider, string? model, string? pluginProviderName = null)
    {
        var providerLabel = string.IsNullOrWhiteSpace(pluginProviderName)
            ? SessionProviderCatalog.Resolve(provider).Label
            : pluginProviderName;
        return provider is SessionProvider.ClaudeCli || string.IsNullOrWhiteSpace(model)
            ? $"{label} ({providerLabel})"
            : $"{label} ({providerLabel} - {model})";
    }

    /// <summary>The model of a profile's local provider, or <see langword="null"/> for a Claude profile.</summary>
    public static string? ModelOf(SessionProfile profile) => profile.ProviderConfig switch
    {
        OllamaConfig ollama => ollama.Model,
        LmStudioConfig lmStudio => lmStudio.Model,
        _ => null,
    };
}
