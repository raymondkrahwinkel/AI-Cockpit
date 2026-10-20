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

    // Why this profile may not start, or null when it may. `stated` are the models the start itself names.
    public static string? Refusal(ProvisionPolicy? policy, SessionProfile? profile, params string?[] stated) =>
        Check(policy, profile, stated).Refusal;

    // Every model a start could end up on is checked: drivers differ in which of them wins, so one outside the list
    // refuses. Under a listed set the start is pinned to an allowed model, so no provider default runs instead.
    public static (string? Refusal, string? Model) Check(ProvisionPolicy? policy, SessionProfile? profile, params string?[] stated)
    {
        if (policy?.Providers?.Allowed is not { } allowed)
        {
            return (null, null);
        }

        var label = profile?.Label ?? "This profile";
        var ids = IdsOf(profile);
        if (allowed.FirstOrDefault(entry => ids.Contains(entry.Id, StringComparer.OrdinalIgnoreCase)) is not { } entry)
        {
            return ($"{label} does not start: its provider is not allowed by {policy.By}.", null);
        }

        var candidates = stated.Concat(_ModelsOf(profile))
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.FirstOrDefault(model => !_AllowsModel(entry.Models, model)) is { } refused)
        {
            return ($"{label} does not start on {refused}: {policy.By} does not allow that model.", null);
        }

        if (entry.Models.ValueKind is not JsonValueKind.Array)
        {
            return (null, null);
        }

        return (candidates.FirstOrDefault() ?? _Listed(entry.Models).FirstOrDefault()) is { } pinned
            ? (null, pinned)
            : ($"{label} does not start: {policy.By} allows no model for its provider.", null);
    }

    // A live switch is checked like a start, and clearing the model under a listed set is refused: the provider's
    // own default would run, which the organisation never listed.
    public static string? SwitchRefusal(ProvisionPolicy? policy, SessionProfile? profile, string? model) =>
        Check(policy, profile, model) is { Refusal: { } refusal } ? refusal
        : string.IsNullOrWhiteSpace(model) && Check(policy, profile).Model is not null
            ? $"The model cannot be cleared: {policy?.By} allows only listed models."
            : null;

    public static string NotAllowedLabel(ProvisionPolicy policy) => $"Not allowed by {policy.By}";

    // The models a profile names for itself, where the host can see them; a plugin keeps its own in its config.
    private static IEnumerable<string?> _ModelsOf(SessionProfile? profile) => profile?.ProviderConfig switch
    {
        OllamaConfig ollama => [ollama.Model],
        LmStudioConfig lmStudio => [lmStudio.Model],
        PluginProviderConfig plugin => [profile.Defaults?.OptionDefaults?.GetValueOrDefault("model"), .. _ConfigModelsOf(plugin.ConfigJson)],
        _ => [profile?.Defaults?.OptionDefaults?.GetValueOrDefault("model")],
    };

    // OpenRouter, Gemini, Grok, GitHub Models and Codex keep `model`, Kimi and opencode `defaultModel`.
    private static IEnumerable<string?> _ConfigModelsOf(string configJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson);
            return document.RootElement.ValueKind is JsonValueKind.Object
                ? [.. document.RootElement.EnumerateObject()
                    .Where(property => property.Value.ValueKind is JsonValueKind.String
                        && (string.Equals(property.Name, "model", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(property.Name, "defaultModel", StringComparison.OrdinalIgnoreCase)))
                    .Select(property => property.Value.GetString())]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<string> _Listed(JsonElement models) =>
        models.EnumerateArray().Select(item => item.ValueKind is JsonValueKind.String ? item.GetString() : null)
            .Where(model => !string.IsNullOrWhiteSpace(model)).Select(model => model!.Trim());

    private static bool _AllowsModel(JsonElement models, string model) => models.ValueKind switch
    {
        JsonValueKind.String => models.GetString() == "*",
        JsonValueKind.Array => _Listed(models).Contains(model, StringComparer.OrdinalIgnoreCase),
        _ => false,
    };
}
