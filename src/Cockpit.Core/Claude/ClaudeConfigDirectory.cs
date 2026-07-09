using Cockpit.Core.Profiles;

namespace Cockpit.Core.Claude;

/// <summary>
/// Resolves the claude config directory a session's transcript JSONL lives under — the piece the
/// read-aloud and status tailers need to locate <c>projects/*/{session-id}.jsonl</c>. A profile pins
/// its own directory via <c>CLAUDE_CONFIG_DIR</c>; a profile-less session inherits the cockpit's own
/// environment, so it resolves to <c>CLAUDE_CONFIG_DIR</c> when set, otherwise the CLI default
/// <c>~/.claude</c> — matching exactly where the spawned CLI writes its transcript when no profile
/// overrides it (<see cref="Tty.TtyEnvironment"/> only sets <c>CLAUDE_CONFIG_DIR</c> under a profile).
/// </summary>
public static class ClaudeConfigDirectory
{
    public const string EnvironmentVariable = "CLAUDE_CONFIG_DIR";

    public static string Resolve(ClaudeProfile? profile, string? environmentConfigDir, string userProfileDirectory)
    {
        if (profile is not null)
        {
            return profile.ConfigDir;
        }

        return string.IsNullOrWhiteSpace(environmentConfigDir)
            ? Path.Combine(userProfileDirectory, ".claude")
            : environmentConfigDir;
    }
}
