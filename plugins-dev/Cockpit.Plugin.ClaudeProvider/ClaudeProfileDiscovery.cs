using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// The Claude plugin's own login-gate and self-detection (weg A) — the two provider-specific behaviours the
// host used to hold in-tree (`ClaudeProfileLoginChecker` / `ClaudeCliProfileDetector`). The plugin
// owns them now so the core knows nothing of `.credentials.json` or `~/.claude` directory layout;
// the host dispatches to these through the generic `TtyProviderRegistration.IsLoggedIn`/`DetectProfiles`
// seams.
internal static class ClaudeProfileDiscovery
{
    // Whether this profile is logged in, per the CLI's own `auth status` — see `ClaudeLoginStatus`, which owns
    // the cache that keeps this answer immediate on the UI thread.
    //
    // This used to be `File.Exists(".credentials.json")`, which was wrong in both directions: on macOS the
    // credentials live in the Keychain and that file never appears, and an expired token leaves it in place
    // (AC-629). Nothing here reads a credential's contents either way (Iron Law #8).
    public static bool IsLoggedIn(string configJson, Func<string, string?>? managedResolver = null) =>
        ClaudeLoginStatus.IsLoggedIn(configJson, managedResolver);

    // Discovers the well-known Claude config directories on this machine (`~/.claude`,
    // `~/.claude-personal`, `~/.claude-work`) that actually exist, minting a profile per surviving
    // directory labelled from its name (`.claude` → `default`, `.claude-work` → `work`).
    // The config JSON pins the discovered directory unless it is the CLI default, which stays blank so the
    // profile follows `~/.claude` wherever the CLI puts it.
    public static IReadOnlyList<PluginDetectedProfile> Detect() =>
        Detect(_DefaultCandidateDirectories(), Directory.Exists);

    // Test seam: detect against an arbitrary candidate set and existence check.
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
