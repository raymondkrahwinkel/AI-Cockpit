namespace Cockpit.Plugin.ClaudeProvider;

// Resolves the claude config directory rules this plugin needs — a copy of the host's
// `ClaudeConfigDirectory` against this plugin's own config shape (weg A: the plugin owns its machinery,
// and cannot reference the core type). The subtlety that makes the copy worth it: exporting
// `CLAUDE_CONFIG_DIR=~/.claude` is *not* a no-op — the CLI keeps `.claude.json` in the home
// root when the variable is unset but inside the directory when it is set, so a default-dir profile must leave
// it unset or a freshly logged-in CLI re-onboards.
internal static class ClaudeConfigPaths
{
    public const string EnvironmentVariable = "CLAUDE_CONFIG_DIR";

    // The value to export as `EnvironmentVariable`, or `null` to leave it unset (a default-dir or config-less session).
    public static string? ResolveSpawnOverride(string? configDir, string userProfileDirectory) =>
        string.IsNullOrWhiteSpace(configDir) || IsDefaultDirectory(configDir, userProfileDirectory) ? null : configDir;

    // The directory whose `.claude.json` the spawned CLI actually reads — the profile dir for a non-default profile, the home root otherwise (workspace-trust and the statusline settings both live here).
    public static string ResolveConfigJsonDirectory(string? configDir, string userProfileDirectory) =>
        ResolveSpawnOverride(configDir, userProfileDirectory) ?? userProfileDirectory;

    // The directory the CLI keeps its session state under — `projects/*/*.jsonl` transcripts and
    // `.credentials.json` both live here. A pinned profile keeps its own dir; a blank/default profile
    // resolves to `CLAUDE_CONFIG_DIR` when set, else the CLI default `~/.claude`. Unlike
    // `ResolveConfigJsonDirectory`, a default profile resolves to `~/.claude` (where the
    // transcripts and credentials live), not the home root — the CLI writes `.claude.json` to the root
    // but everything else under `~/.claude`.
    public static string ResolveStateDirectory(string? configDir, string? environmentConfigDir, string userProfileDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return configDir;
        }

        return string.IsNullOrWhiteSpace(environmentConfigDir)
            ? Path.Combine(userProfileDirectory, ".claude")
            : environmentConfigDir;
    }

    private static bool IsDefaultDirectory(string configDir, string userProfileDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Normalize(configDir), Normalize(Path.Combine(userProfileDirectory, ".claude")), comparison);
    }

    private static string Normalize(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
