using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Core.Profiles;

// Mints the plugin config a Claude profile runs under, now that Claude is a bundled provider plugin (phase 4).
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

        // AC-1585: case-insensitive, as the plugin reads it, so what the host checks is what the plugin runs.
        try
        {
            var root = JsonNode.Parse(configJson, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject;
            var configDir = root?["configDir"] is JsonValue dir && dir.TryGetValue<string>(out var dirText) ? dirText : null;
            var executablePath = root?["executablePath"] is JsonValue exe && exe.TryGetValue<string>(out var exeText) ? exeText : null;
            return new ClaudeConfig(configDir ?? string.Empty, string.IsNullOrWhiteSpace(executablePath) ? null : executablePath);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return new ClaudeConfig(string.Empty);
        }
    }

    // One-time migration of legacy typed permission-mode/model/effort defaults into `OptionDefaults` (phase 4): a non-blank
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
