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
    /// Carries a Claude profile's typed permission-mode/model/effort defaults into the generic
    /// <see cref="ProfileDefaults.OptionDefaults"/> map when it has none yet — the defaults half of the migration, so a
    /// profile keeps its saved start settings after Claude becomes a plugin (the profile-edit and New-session dialogs
    /// read them generically now). Idempotent: a profile that already has <see cref="ProfileDefaults.OptionDefaults"/>
    /// is returned untouched.
    /// </summary>
    public static ProfileDefaults WithMigratedOptionDefaults(ProfileDefaults defaults)
    {
        if (defaults.OptionDefaults is { Count: > 0 })
        {
            return defaults;
        }

        // The Claude plugin declares its options under these keys (matching the host's WellKnownPluginSessionOptions);
        // Core cannot reference the plugin abstractions, so these literals are the one Claude-specific detail here.
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(defaults.PermissionMode))
        {
            options["permission-mode"] = defaults.PermissionMode;
        }

        if (!string.IsNullOrWhiteSpace(defaults.Model))
        {
            options["model"] = defaults.Model;
        }

        if (!string.IsNullOrWhiteSpace(defaults.Effort))
        {
            options["effort"] = defaults.Effort;
        }

        return options.Count > 0 ? defaults with { OptionDefaults = options } : defaults;
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
