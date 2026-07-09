using Cockpit.Core.Profiles;

namespace Cockpit.Core.Claude.Tty;

/// <summary>
/// Composes the environment block for a <c>claude</c> process spawned inside a ConPTY (TTY mode).
/// Pure and side-effect-free so the composition rules are unit-testable without reading the real
/// process environment.
/// </summary>
/// <remarks>
/// Unlike the SDK-mode spawn (<c>ClaudeCliProcess</c>, which uses <c>Process</c> and inherits the
/// parent environment automatically), a ConPTY child receives <em>only</em> the environment block
/// we hand it — there is no implicit inheritance. So the base map must start from the parent
/// process's own variables (HOME/USERPROFILE, PATH, APPDATA, ...) or <c>claude</c> loses the very
/// things it needs to find its config, credentials and node runtime.
/// </remarks>
public static class TtyEnvironment
{
    /// <summary>
    /// Value for <c>TERM</c>: an xterm-256color pseudo-terminal is what makes <c>claude</c>'s Ink
    /// TUI see <c>isTTY=true</c> and render the interactive interface instead of crashing with
    /// "Raw mode is not supported" (the non-TTY/piping artefact).
    /// </summary>
    public const string TermValue = "xterm-256color";

    /// <summary>
    /// Builds the environment for the pty child: everything in <paramref name="baseEnvironment"/>
    /// (the inherited parent environment), then <c>TERM</c>, then — when <paramref name="profile"/>
    /// is non-null — <c>CLAUDE_CONFIG_DIR</c> so the spawned CLI reads that profile's own
    /// login/config. Never sets <c>ANTHROPIC_API_KEY</c> (that would switch the CLI to API-key
    /// billing instead of the subscription route — same rule as the SDK spawn).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(
        IReadOnlyDictionary<string, string> baseEnvironment,
        ClaudeProfile? profile)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in baseEnvironment)
        {
            // Drop the markers of the Claude Code session the cockpit itself was launched from. When the
            // cockpit is started from inside a Claude Code session (a claude terminal, an agent), the
            // inherited CLAUDE_CODE_SESSION_ID would make the spawned CLI adopt that session id — writing
            // its turns into the parent's transcript instead of its own, so the read-aloud/status tailers
            // (which look for the session's own new transcript) never find one. A normal launch has none
            // of these set, so this is a no-op there.
            if (IsNestedClaudeCodeMarker(key))
            {
                continue;
            }

            environment[key] = value;
        }

        environment["TERM"] = TermValue;

        if (profile is not null)
        {
            environment["CLAUDE_CONFIG_DIR"] = profile.ConfigDir;
        }

        return environment;
    }

    /// <summary>
    /// True for the environment variables a running Claude Code session exports to mark itself
    /// (<c>CLAUDECODE</c>, <c>CLAUDE_CODE_*</c>, <c>CLAUDE_AGENT_*</c>) — notably
    /// <c>CLAUDE_CODE_SESSION_ID</c>. Stripped before spawning so a cockpit launched from within such a
    /// session does not hand its own session identity down to the child CLI. <c>CLAUDE_CONFIG_DIR</c> is
    /// deliberately not matched (it does not start with <c>CLAUDE_CODE</c>) and is re-applied per profile.
    /// </summary>
    public static bool IsNestedClaudeCodeMarker(string key) =>
        key.StartsWith("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("CLAUDE_AGENT_", StringComparison.OrdinalIgnoreCase);
}
