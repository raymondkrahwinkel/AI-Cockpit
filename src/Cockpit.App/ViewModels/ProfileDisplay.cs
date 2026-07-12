using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Formats a profile's label for the profile list and pickers with its provider — and the model for a
/// local provider (#26): e.g. <c>default (Claude CLI)</c> or <c>local (LM Studio - qwen2.5)</c>, so it is
/// clear at a glance which backend (and which local model) a profile runs.
/// </summary>
public static class ProfileDisplay
{
    public static string Format(string label, SessionProvider provider, string? model)
    {
        var providerLabel = SessionProviderCatalog.Resolve(provider).Label;
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
