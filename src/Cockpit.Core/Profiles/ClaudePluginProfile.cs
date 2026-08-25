using System.Text.Json;

namespace Cockpit.Core.Profiles;

// Mints the plugin config a Claude profile runs under, now that Claude is a bundled provider plugin (Fase 4).
// Migrates a profile stored as first-class Claude, or auto-detected from `~/.claude*`, into a plugin profile on load.
// Idempotent: a profile already on this plugin passes through unchanged.
public static class ClaudePluginProfile
{
    // The id the bundled Claude provider plugin registers its session and TTY routes under.
    public const string ProviderId = "claude";

    // Builds the plugin config for a Claude profile from the two settings its in-tree `ClaudeConfig` carried.
    public static PluginProviderConfig Create(string? configDir, string? executablePath) =>
        new(ProviderId, _SerializeConfig(configDir, executablePath));

    // Reconstructs a `ClaudeConfig` from the plugin's `{configDir,executablePath}` blob (inverse of `Create`), so host-side
    // Claude features (status tailer, login check) that ask a profile for `SessionProfile.Claude` don't see `null` and
    // fall back to tailing the wrong `~/.claude` directory for a migrated non-default profile. Pass only this plugin's own config.
    public static ClaudeConfig ReadClaudeConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return new ClaudeConfig(string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
            var root = document.RootElement;
            var configDir = root.TryGetProperty("configDir", out var dir) && dir.ValueKind == JsonValueKind.String ? dir.GetString() : null;
            var executablePath = root.TryGetProperty("executablePath", out var exe) && exe.ValueKind == JsonValueKind.String ? exe.GetString() : null;
            return new ClaudeConfig(configDir ?? string.Empty, string.IsNullOrWhiteSpace(executablePath) ? null : executablePath);
        }
        catch (JsonException)
        {
            return new ClaudeConfig(string.Empty);
        }
    }

    // One-time migration of legacy typed permission-mode/model/effort defaults into `OptionDefaults` (Fase 4): a non-blank
    // legacy value wins, recovering profiles where an earlier build seeded OptionDefaults with the plugin's own defaults
    // instead of the operator's; becomes a no-op once resaved. Key literals mirror the host's `WellKnownPluginSessionOptions`.
    public static ProfileDefaults WithMigratedOptionDefaults(ProfileDefaults defaults)
    {
        var options = defaults.OptionDefaults is { Count: > 0 } existing
            ? new Dictionary<string, string>(existing, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        // Only a non-blank legacy field carries a value across; a blank one leaves whatever OptionDefaults already
        // holds (so a re-saved, migrated profile with blank legacy fields keeps its OptionDefaults untouched).
        _ApplyLegacyDefault(options, "permission-mode", defaults.PermissionMode);
        _ApplyLegacyDefault(options, "model", defaults.Model);
        _ApplyLegacyDefault(options, "effort", defaults.Effort);

        return options.Count > 0 ? defaults with { OptionDefaults = options } : defaults with { OptionDefaults = null };
    }

    private static void _ApplyLegacyDefault(Dictionary<string, string> options, string key, string legacyValue)
    {
        if (!string.IsNullOrWhiteSpace(legacyValue))
        {
            options[key] = legacyValue;
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
