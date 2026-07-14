namespace Cockpit.Core.Sessions.Tty;

/// <summary>
/// Composes the environment block for any CLI spawned inside a pseudo console (TTY mode): the host's base
/// (<see cref="BuildBase"/>) plus a provider's overlay (<see cref="Compose"/>). Pure and side-effect-free so
/// the composition rules are unit-testable without reading the real process environment.
/// </summary>
/// <remarks>
/// Unlike the SDK-mode spawn (<c>ClaudeCliProcess</c>, which uses <c>Process</c> and inherits the
/// parent environment automatically), a ConPTY child receives <em>only</em> the environment block
/// we hand it — there is no implicit inheritance. So the base map must start from the parent
/// process's own variables (HOME/USERPROFILE, PATH, APPDATA, ...) or the CLI loses the very
/// things it needs to find its config, credentials and runtime.
/// <para>
/// What each provider adds on top lives with that provider (<c>ClaudeTtyEnvironment</c> for <c>claude</c>) —
/// the base and the scrub are the host's, and stay in one place.
/// </para>
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
    /// The environment every pty child starts from, whichever CLI it runs: the inherited parent environment
    /// minus what must not be handed down, plus <c>TERM</c> and a UTF-8 locale.
    /// <para>
    /// What it strips is a host rule, not a provider's: the markers of the agent session the cockpit itself was
    /// launched from, the host terminal's self-identification, and any inherited Anthropic credential (which
    /// would silently move a session onto API-key billing — see <see cref="IsAnthropicCredentialMarker"/>).
    /// A provider adds to this map through <see cref="Abstractions.Sessions.TtyLaunchSpec.EnvironmentOverlay"/>;
    /// it cannot take away from it, because a scrub that each provider could opt out of is not a scrub.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildBase(IReadOnlyDictionary<string, string> parentEnvironment)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parentEnvironment)
        {
            // What the host owns is never handed down: the markers of the agent session the cockpit was launched
            // from (an inherited CLAUDE_CODE_SESSION_ID would make the child adopt that session id and write its
            // turns into the parent's transcript), the host terminal's self-identification (the child is rendered
            // by Exclr8, not by whatever terminal launched Cockpit), and any Anthropic credential (which would
            // move the session onto API-key billing). A normal desktop launch has none of these, so this is a
            // no-op there; it bites exactly when the cockpit is started from a shell that exports one.
            if (IsHostControlled(key))
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

        return environment;
    }

    /// <summary>
    /// Lays a provider's overlay over the base: a value sets, <see langword="null"/> removes.
    /// <para>
    /// A provider cannot reinstate what the host stripped. Keys the host owns (<see cref="IsHostControlled"/>)
    /// are ignored in an overlay — otherwise the scrub would be advisory, and a provider could hand the child an
    /// <c>ANTHROPIC_API_KEY</c> that silently moves the session onto API-key billing. Removing them stays
    /// allowed, because removing something already absent asks for nothing.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Compose(
        IReadOnlyDictionary<string, string> baseEnvironment,
        IReadOnlyDictionary<string, string?> overlay)
    {
        var environment = new Dictionary<string, string>(baseEnvironment, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overlay)
        {
            if (value is null)
            {
                environment.Remove(key);
                continue;
            }

            if (IsHostControlled(key))
            {
                continue;
            }

            environment[key] = value;
        }

        return environment;
    }

    /// <summary>
    /// The keys an overlay tried to set but does not get to (<see cref="IsHostControlled"/>). Pure, so the
    /// composition stays testable; the launcher logs them, because a security rule that fires silently is one
    /// nobody finds out about until it matters.
    /// </summary>
    public static IReadOnlyList<string> RejectedOverlayKeys(IReadOnlyDictionary<string, string?> overlay) =>
        [.. overlay.Where(entry => entry.Value is not null && IsHostControlled(entry.Key)).Select(entry => entry.Key)];

    /// <summary>
    /// True for a variable the host decides about, not a provider: the markers of the agent session the cockpit
    /// was launched from, the host terminal's self-identification, and any Anthropic credential. These are
    /// stripped from the inherited environment and cannot be put back by an overlay.
    /// </summary>
    public static bool IsHostControlled(string key) =>
        IsNestedClaudeCodeMarker(key)
        || IsHostTerminalIdentityMarker(key)
        || IsAnthropicCredentialMarker(key);

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

    /// <summary>
    /// True for an Anthropic credential in the environment (<c>ANTHROPIC_API_KEY</c>, <c>ANTHROPIC_AUTH_TOKEN</c>,
    /// and the rest of the <c>ANTHROPIC_*</c> family). Two reasons to strip them rather than merely not set them:
    /// a key that reaches the CLI switches the session from the operator's subscription to API-key billing —
    /// silently, and on someone else's invoice — and a credential that the cockpit inherited from whatever
    /// launched it has no business being handed on to a child it did not come from. A normal desktop launch has
    /// none of these set, so this is a no-op there; it bites exactly when the cockpit is started from a shell
    /// that exports one.
    /// </summary>
    public static bool IsAnthropicCredentialMarker(string key) =>
        key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase);
}
