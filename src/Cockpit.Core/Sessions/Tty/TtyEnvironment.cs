using Cockpit.Core.Profiles;

namespace Cockpit.Core.Sessions.Tty;

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
    /// UTF-8 locale forced onto the pty child when the inherited environment has no UTF-8 locale. Claude's
    /// Ink TUI measures character widths through the C library's <c>wcwidth</c>, which only reports correct
    /// widths for wide/box-drawing/emoji glyphs under a UTF-8 <c>LC_CTYPE</c>; under <c>C</c>/<c>POSIX</c> or
    /// a non-UTF-8 locale it miscounts, so the TUI's layout math drifts and frames render overlapping and
    /// misaligned (spaces left showing an earlier frame's box-drawing rules). <c>C.UTF-8</c> is locale-data
    /// free (always available on modern glibc/Fedora) so this is a safe universal fallback.
    /// </summary>
    public const string Utf8LocaleValue = "C.UTF-8";

    /// <summary>
    /// Builds the environment for the pty child: everything in <paramref name="baseEnvironment"/>
    /// (the inherited parent environment), then <c>TERM</c>, then the profile's <c>CLAUDE_CONFIG_DIR</c>.
    /// A profile on a non-default directory exports it; a profile pinned to the CLI's default
    /// (<c>~/.claude</c>) clears any inherited value so the CLI uses its native home-root config/login
    /// (setting it to the default dir is not a no-op — see
    /// <see cref="ClaudeConfigDirectory.ResolveSpawnOverride"/>). A profile-less session leaves any
    /// inherited value untouched, since the transcript tailers resolve the dir through that same variable.
    /// Never sets <c>ANTHROPIC_API_KEY</c> (that would switch the CLI to API-key billing instead of the
    /// subscription route — same rule as the SDK spawn).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(
        IReadOnlyDictionary<string, string> baseEnvironment,
        SessionProfile? profile,
        string userProfileDirectory)
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

            // Drop the host terminal emulator's self-identification (see IsHostTerminalIdentityMarker) —
            // the pty child is rendered by Exclr8, not by whatever terminal Cockpit itself runs in.
            if (IsHostTerminalIdentityMarker(key))
            {
                continue;
            }

            environment[key] = value;
        }

        environment["TERM"] = TermValue;

        // Guarantee a UTF-8 ctype so claude's TUI measures glyph widths correctly (see Utf8LocaleValue). Only
        // steps in when the inherited locale is missing or non-UTF-8 — a machine already on a UTF-8 locale
        // (e.g. en_US.UTF-8) keeps it. Forces LC_ALL so it wins over any non-UTF-8 LC_CTYPE/LC_ALL below it.
        if (!HasUtf8Locale(environment))
        {
            environment["LC_ALL"] = Utf8LocaleValue;
            environment["LANG"] = Utf8LocaleValue;
        }

        if (profile is not null)
        {
            var configDirOverride = ClaudeConfigDirectory.ResolveSpawnOverride(profile, userProfileDirectory);
            if (configDirOverride is not null)
            {
                environment[ClaudeConfigDirectory.EnvironmentVariable] = configDirOverride;
            }
            else
            {
                environment.Remove(ClaudeConfigDirectory.EnvironmentVariable);
            }

            // A memory ceiling, when the profile asks for one. Off unless it does: a capped session that needs more
            // memory than the cap does not slow down, it dies mid-turn.
            if (SessionMemoryLimit.NodeOptions(environment.GetValueOrDefault("NODE_OPTIONS"), profile.MemoryLimitMb) is { } options)
            {
                environment["NODE_OPTIONS"] = options;
            }
        }

        return environment;
    }

    /// <summary>
    /// True when the effective ctype locale is UTF-8. The C library resolves the ctype category as
    /// <c>LC_ALL</c> (if set) else <c>LC_CTYPE</c> else <c>LANG</c>, so this checks them in that precedence
    /// and treats a value containing <c>UTF-8</c>/<c>UTF8</c> (case-insensitive) as UTF-8.
    /// </summary>
    private static bool HasUtf8Locale(IReadOnlyDictionary<string, string> environment)
    {
        var effective =
            Value(environment, "LC_ALL")
            ?? Value(environment, "LC_CTYPE")
            ?? Value(environment, "LANG");

        return effective is not null
            && (effective.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)
                || effective.Contains("UTF8", StringComparison.OrdinalIgnoreCase));

        static string? Value(IReadOnlyDictionary<string, string> env, string key) =>
            env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
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

    /// <summary>
    /// True for the environment variables a host terminal emulator uses to self-identify to child
    /// processes — <c>TERM_PROGRAM</c>/<c>TERM_PROGRAM_VERSION</c> (set by most modern terminals,
    /// including Ghostty, which sets <c>TERM_PROGRAM=ghostty</c>) and Ghostty's own <c>GHOSTTY_*</c>
    /// variables (e.g. <c>GHOSTTY_RESOURCES_DIR</c>, <c>GHOSTTY_BIN_DIR</c>). Stripped for the same
    /// reason <see cref="TermValue"/> pins <c>TERM</c> to a generic value: the pty child (<c>claude</c>)
    /// is actually rendered by Cockpit's own Exclr8 terminal emulator, not by whatever terminal
    /// launched Cockpit. If <c>TERM_PROGRAM=ghostty</c> leaked through, claude's Ink TUI would detect
    /// "running inside Ghostty" and pick a Ghostty-specific render path (advanced escape sequences
    /// Ghostty supports) that Exclr8 does not match, causing a vertical render desync (input echo
    /// jumping to the top row instead of tracking the cursor).
    /// <c>TERMINFO</c>/<c>TERMINFO_DIRS</c> are deliberately NOT matched here: claude's Ink TUI is a
    /// Node.js process that does not consult the ncurses terminfo database for its own rendering
    /// decisions, so a Ghostty-pointed <c>TERMINFO_DIRS</c> does not reproduce this bug — scrubbing it
    /// would only risk breaking an unrelated subprocess that does shell out to a terminfo-aware tool.
    /// <c>COLORTERM</c> is also deliberately not matched — it is a generic truecolor-support signal
    /// (not a terminal-identity marker) and Exclr8 does support truecolor, so it should pass through.
    /// </summary>
    public static bool IsHostTerminalIdentityMarker(string key) =>
        key.Equals("TERM_PROGRAM", StringComparison.OrdinalIgnoreCase)
        || key.Equals("TERM_PROGRAM_VERSION", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("GHOSTTY_", StringComparison.OrdinalIgnoreCase);
}
