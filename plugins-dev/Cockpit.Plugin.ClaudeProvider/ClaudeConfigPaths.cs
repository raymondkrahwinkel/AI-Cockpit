namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Resolves the claude config directory rules this plugin needs — a copy of the host's
/// <c>ClaudeConfigDirectory</c> against this plugin's own config shape (weg A: the plugin owns its machinery,
/// and cannot reference the core type). The subtlety that makes the copy worth it: exporting
/// <c>CLAUDE_CONFIG_DIR=~/.claude</c> is <em>not</em> a no-op — the CLI keeps <c>.claude.json</c> in the home
/// root when the variable is unset but inside the directory when it is set, so a default-dir profile must leave
/// it unset or a freshly logged-in CLI re-onboards.
/// </summary>
internal static class ClaudeConfigPaths
{
    public const string EnvironmentVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>The value to export as <see cref="EnvironmentVariable"/>, or <see langword="null"/> to leave it unset (a default-dir or config-less session).</summary>
    public static string? ResolveSpawnOverride(string? configDir, string userProfileDirectory) =>
        string.IsNullOrWhiteSpace(configDir) || IsDefaultDirectory(configDir, userProfileDirectory) ? null : configDir;

    /// <summary>The directory whose <c>.claude.json</c> the spawned CLI actually reads — the profile dir for a non-default profile, the home root otherwise (workspace-trust and the statusline settings both live here).</summary>
    public static string ResolveConfigJsonDirectory(string? configDir, string userProfileDirectory) =>
        ResolveSpawnOverride(configDir, userProfileDirectory) ?? userProfileDirectory;

    private static bool IsDefaultDirectory(string configDir, string userProfileDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Normalize(configDir), Normalize(Path.Combine(userProfileDirectory, ".claude")), comparison);
    }

    private static string Normalize(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
