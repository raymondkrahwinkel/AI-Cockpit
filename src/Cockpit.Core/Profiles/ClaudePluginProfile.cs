using System.Text.Json;

namespace Cockpit.Core.Profiles;

/// <summary>
/// Mints the <see cref="PluginProviderConfig"/> a Claude profile runs under now that Claude is a bundled provider
/// plugin (Fase 4). The Claude plugin registers under the id <c>claude</c> and reads a
/// <c>{"configDir","executablePath"}</c> blob from the opaque config JSON the host round-trips; this is the one place
/// the host mints that blob, so a profile loaded from an older config (which stored Claude as a first-class provider)
/// or auto-detected from a well-known <c>~/.claude*</c> directory becomes a plugin profile on load. Idempotent: a
/// profile already stored as this plugin is read back as-is and never passes through here.
/// </summary>
public static class ClaudePluginProfile
{
    /// <summary>The id the bundled Claude provider plugin registers its session and TTY routes under.</summary>
    public const string ProviderId = "claude";

    /// <summary>Builds the plugin config for a Claude profile from the two settings its in-tree <c>ClaudeConfig</c> carried.</summary>
    public static PluginProviderConfig Create(string? configDir, string? executablePath) =>
        new(ProviderId, _SerializeConfig(configDir, executablePath));

    /// <summary>
    /// Keeps a Claude profile's generic <see cref="ProfileDefaults.OptionDefaults"/> in sync with its authoritative
    /// typed permission-mode/model/effort defaults — the profile-edit dialog writes both together, and the typed
    /// fields win here, so a profile keeps its saved start settings and recovers if an earlier build seeded
    /// OptionDefaults with the plugin's own defaults instead of the operator's values. Keys a provider owns itself (a
    /// sandbox, say) pass through untouched; a blank typed field leaves its key unset (the option's own default then
    /// applies). Core cannot reference the plugin abstractions, so these key literals — matching the host's
    /// <c>WellKnownPluginSessionOptions</c> — are the one Claude-specific detail here.
    /// </summary>
    public static ProfileDefaults WithMigratedOptionDefaults(ProfileDefaults defaults)
    {
        var options = defaults.OptionDefaults is { Count: > 0 } existing
            ? new Dictionary<string, string>(existing, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        _ApplyTypedDefault(options, "permission-mode", defaults.PermissionMode);
        _ApplyTypedDefault(options, "model", defaults.Model);
        _ApplyTypedDefault(options, "effort", defaults.Effort);

        return options.Count > 0 ? defaults with { OptionDefaults = options } : defaults with { OptionDefaults = null };
    }

    private static void _ApplyTypedDefault(Dictionary<string, string> options, string key, string typedValue)
    {
        if (string.IsNullOrWhiteSpace(typedValue))
        {
            options.Remove(key);
        }
        else
        {
            options[key] = typedValue;
        }
    }

    // Matches the plugin's own ClaudeProviderConfig shape: camelCase keys, blank fields omitted (both blank means a
    // default session against the machine's own ~/.claude login).
    private static string _SerializeConfig(string? configDir, string? executablePath)
    {
        var config = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            config["configDir"] = configDir.Trim();
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            config["executablePath"] = executablePath.Trim();
        }

        return JsonSerializer.Serialize(config);
    }
}
