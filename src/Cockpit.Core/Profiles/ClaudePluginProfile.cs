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
