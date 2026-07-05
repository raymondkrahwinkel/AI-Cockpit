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
            environment[key] = value;
        }

        environment["TERM"] = TermValue;

        if (profile is not null)
        {
            environment["CLAUDE_CONFIG_DIR"] = profile.ConfigDir;
        }

        return environment;
    }
}
