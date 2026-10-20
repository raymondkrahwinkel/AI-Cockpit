using System.Text.Json;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Provisioning;

// AC-1593 (manifest-v1 §3.4, §3.7): which providers and models a session may start on. A profile outside the list is
// kept, not removed; it simply does not start, and says why.
public static class OrgProviderPolicy
{
    // The ids a profile's provider answers to in a manifest. Claude's plugin id is `claude`, its manifest id `claude-provider`.
    public static IReadOnlyList<string> IdsOf(SessionProfile? profile) => profile?.ProviderConfig switch
    {
        PluginProviderConfig { ProviderId: ClaudePluginProfile.ProviderId } or null or ClaudeConfig => ["claude-provider", ClaudePluginProfile.ProviderId],
        PluginProviderConfig plugin => [plugin.ProviderId],
        OllamaConfig => ["ollama"],
        LmStudioConfig => ["lmstudio"],
        _ => [],
    };

    // The model a profile names for itself, where the host can see it; a provider's own default is not visible here.
    public static string? ModelOf(SessionProfile? profile) => profile?.ProviderConfig switch
    {
        OllamaConfig ollama => ollama.Model,
        LmStudioConfig lmStudio => lmStudio.Model,
        _ => profile?.Defaults?.OptionDefaults?.GetValueOrDefault("model"),
    };

    // Why this profile may not start (with `model`, when one is stated), or null when it may.
    public static string? Refusal(ProvisionPolicy? policy, SessionProfile? profile, string? model)
    {
        if (policy?.Providers?.Allowed is not { } allowed)
        {
            return null;
        }

        var ids = IdsOf(profile);
        if (allowed.FirstOrDefault(entry => ids.Contains(entry.Id, StringComparer.OrdinalIgnoreCase)) is not { } entry)
        {
            return $"{profile?.Label ?? "This profile"} does not start: its provider is not allowed by {policy.By}.";
        }

        return string.IsNullOrWhiteSpace(model) || _AllowsModel(entry.Models, model.Trim())
            ? null
            : $"{profile?.Label ?? "This profile"} does not start on {model}: {policy.By} does not allow that model.";
    }

    public static string NotAllowedLabel(ProvisionPolicy policy) => $"Not allowed by {policy.By}";

    private static bool _AllowsModel(JsonElement models, string model) => models.ValueKind switch
    {
        JsonValueKind.String => models.GetString() == "*",
        JsonValueKind.Array => models.EnumerateArray().Any(item => string.Equals(item.GetString(), model, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };
}
