using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// The Claude plugin's own login-gate and self-detection (weg A) — the two provider-specific behaviours the
/// host used to hold in-tree (<c>ClaudeProfileLoginChecker</c> / <c>ClaudeCliProfileDetector</c>). The plugin
/// owns them now so the core knows nothing of <c>.credentials.json</c> or <c>~/.claude</c> directory layout;
/// the host dispatches to these through the generic <c>TtyProviderRegistration.IsLoggedIn</c>/<c>DetectProfiles</c>
/// seams.
/// </summary>
internal static class ClaudeProfileDiscovery
{
    /// <summary>
    /// True when the profile's config directory holds a <c>.credentials.json</c>. Existence-only, never reading
    /// the file's contents (Iron Law #8 — do not print/inspect secret values). A blank/default profile resolves
    /// to <c>~/.claude</c>, where the CLI keeps credentials.
    /// </summary>
    public static bool IsLoggedIn(string configJson)
    {
        var configDir = ClaudeConfigPaths.ResolveStateDirectory(
            ClaudeProviderConfig.Parse(configJson).ConfigDir,
            Environment.GetEnvironmentVariable(ClaudeConfigPaths.EnvironmentVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        return File.Exists(Path.Combine(configDir, ".credentials.json"));
    }

    /// <summary>
    /// Discovers the well-known Claude config directories on this machine (<c>~/.claude</c>,
    /// <c>~/.claude-personal</c>, <c>~/.claude-work</c>) that actually exist, minting a profile per surviving
    /// directory labelled from its name (<c>.claude</c> → <c>default</c>, <c>.claude-work</c> → <c>work</c>).
    /// The config JSON pins the discovered directory unless it is the CLI default, which stays blank so the
    /// profile follows <c>~/.claude</c> wherever the CLI puts it.
    /// </summary>
    public static IReadOnlyList<PluginDetectedProfile> Detect() =>
        Detect(_DefaultCandidateDirectories(), Directory.Exists);

    /// <summary>Test seam: detect against an arbitrary candidate set and existence check.</summary>
    public static IReadOnlyList<PluginDetectedProfile> Detect(IEnumerable<string> candidateConfigDirs, Func<string, bool> directoryExists)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profiles = new List<PluginDetectedProfile>();

        foreach (var configDir in candidateConfigDirs)
        {
            if (!directoryExists(configDir))
            {
                continue;
            }

            // A default-dir profile keeps a blank ConfigDir so it follows the CLI default; a named dir is pinned.
            var pinnedDir = ClaudeConfigPaths.ResolveSpawnOverride(configDir, home);
            var config = new ClaudeProviderConfig(ConfigDir: pinnedDir);
            profiles.Add(new PluginDetectedProfile(
                _LabelFromDirectoryName(configDir),
                JsonSerializer.Serialize(config, ClaudeProviderConfig.JsonOptions)));
        }

        return profiles;
    }

    private static string[] _DefaultCandidateDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(home, ".claude"),
            Path.Combine(home, ".claude-personal"),
            Path.Combine(home, ".claude-work"),
        ];
    }

    private static string _LabelFromDirectoryName(string configDir)
    {
        var name = Path.GetFileName(configDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            return configDir;
        }

        // ".claude" -> "default", ".claude-work" -> "work", ".claude-personal" -> "personal".
        var trimmed = name.TrimStart('.');
        const string prefix = "claude-";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[prefix.Length..];
        }

        return trimmed.Equals("claude", StringComparison.OrdinalIgnoreCase) ? "default" : trimmed;
    }
}
