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

    /// <summary>
    /// The directory the CLI keeps its session state under — <c>projects/*/*.jsonl</c> transcripts and
    /// <c>.credentials.json</c> both live here. A pinned profile keeps its own dir; a blank/default profile
    /// resolves to <c>CLAUDE_CONFIG_DIR</c> when set, else the CLI default <c>~/.claude</c>. Unlike
    /// <see cref="ResolveConfigJsonDirectory"/>, a default profile resolves to <c>~/.claude</c> (where the
    /// transcripts and credentials live), not the home root — the CLI writes <c>.claude.json</c> to the root
    /// but everything else under <c>~/.claude</c>.
    /// </summary>
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
