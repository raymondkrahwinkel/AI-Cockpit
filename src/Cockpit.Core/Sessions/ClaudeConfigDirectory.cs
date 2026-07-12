using Cockpit.Core.Profiles;

namespace Cockpit.Core.Sessions;

/// <summary>
/// Resolves the claude config directory a session's transcript JSONL lives under — the piece the
/// read-aloud and status tailers need to locate <c>projects/*/{session-id}.jsonl</c>. A profile pins its
/// own directory; a profile-less session resolves to <c>CLAUDE_CONFIG_DIR</c> when set, otherwise the CLI
/// default <c>~/.claude</c>. The transcript lives under this directory regardless of whether the spawn
/// exports <c>CLAUDE_CONFIG_DIR</c> — a default-dir profile writes to <c>~/.claude/projects</c> either
/// way (see <see cref="ResolveSpawnOverride"/> for the spawn-time set/unset rule).
/// </summary>
public static class ClaudeConfigDirectory
{
    public const string EnvironmentVariable = "CLAUDE_CONFIG_DIR";

    public static string Resolve(SessionProfile? profile, string? environmentConfigDir, string userProfileDirectory)
    {
        if (profile is not null)
        {
            return profile.ConfigDir;
        }

        return string.IsNullOrWhiteSpace(environmentConfigDir)
            ? Path.Combine(userProfileDirectory, ".claude")
            : environmentConfigDir;
    }

    /// <summary>
    /// The value to export as <see cref="EnvironmentVariable"/> when spawning the CLI for
    /// <paramref name="profile"/>, or <c>null</c> to leave the variable unset. Null for a profile-less
    /// session and for a profile pinned to the CLI's own default directory (<c>~/.claude</c>): exporting
    /// <c>CLAUDE_CONFIG_DIR=~/.claude</c> is <em>not</em> a no-op — the CLI keeps <c>.claude.json</c> in the
    /// home root when the variable is unset but inside the directory when it is set, so forcing it onto the
    /// default dir makes a freshly logged-in CLI miss the config/login a bare <c>claude</c> wrote and
    /// re-onboard. A profile on a non-default directory returns that directory.
    /// </summary>
    public static string? ResolveSpawnOverride(SessionProfile? profile, string userProfileDirectory)
    {
        if (profile is null)
        {
            return null;
        }

        return IsDefaultDirectory(profile.ConfigDir, userProfileDirectory) ? null : profile.ConfigDir;
    }

    /// <summary>
    /// The directory whose <c>.claude.json</c> the spawned CLI actually reads/writes for
    /// <paramref name="profile"/> — the counterpart of <see cref="ResolveSpawnOverride"/> for callers that
    /// must touch that file directly (the workspace-trust marker). A non-default profile keeps its own dir;
    /// a default-dir profile (or none) resolves to the home root, matching where the CLI keeps
    /// <c>.claude.json</c> when CLAUDE_CONFIG_DIR is left unset.
    /// </summary>
    public static string ResolveConfigJsonDirectory(SessionProfile? profile, string userProfileDirectory) =>
        ResolveSpawnOverride(profile, userProfileDirectory) ?? userProfileDirectory;

    /// <summary>True when <paramref name="configDir"/> is the CLI's own default config directory (<c>~/.claude</c>).</summary>
    public static bool IsDefaultDirectory(string configDir, string userProfileDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Normalize(configDir), Normalize(Path.Combine(userProfileDirectory, ".claude")), comparison);
    }

    private static string Normalize(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
