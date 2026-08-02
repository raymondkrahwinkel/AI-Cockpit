namespace Cockpit.Core.Sessions.Tty;

// Composes the environment block for any CLI spawned inside a pseudo console (TTY mode): the host's base
// (`BuildBase`) plus a provider's overlay (`Compose`). Pure and side-effect-free so
// the composition rules are unit-testable without reading the real process environment.
// Unlike the SDK-mode spawn (`ClaudeCliProcess`, which uses `Process` and inherits the
// parent environment automatically), a ConPTY child receives *only* the environment block
// we hand it — there is no implicit inheritance. So the base map must start from the parent
// process's own variables (HOME/USERPROFILE, PATH, APPDATA, ...) or the CLI loses the very
// things it needs to find its config, credentials and runtime.
//
// What each provider adds on top lives with that provider (`ClaudeTtyEnvironment` for `claude`) —
// the base and the scrub are the host's, and stay in one place.
public static class TtyEnvironment
{
    // Value for `TERM`: an xterm-256color pseudo-terminal is what makes `claude`'s Ink
    // TUI see `isTTY=true` and render the interactive interface instead of crashing with
    // "Raw mode is not supported" (the non-TTY/piping artefact).
    public const string TermValue = "xterm-256color";

    // UTF-8 locale forced onto the pty child when the inherited environment has no UTF-8 locale. Claude's
    // Ink TUI measures character widths through the C library's `wcwidth`, which only reports correct
    // widths for wide/box-drawing/emoji glyphs under a UTF-8 `LC_CTYPE`; under `C`/`POSIX` or
    // a non-UTF-8 locale it miscounts, so the TUI's layout math drifts and frames render overlapping and
    // misaligned (spaces left showing an earlier frame's box-drawing rules). `C.UTF-8` is locale-data
    // free (always available on modern glibc/Fedora) so this is a safe universal fallback.
    public const string Utf8LocaleValue = "C.UTF-8";

    // The environment every pty child starts from, whichever CLI it runs: the inherited parent environment
    // minus what must not be handed down, plus `TERM` and a UTF-8 locale.
    //
    // What it strips is a host rule, not a provider's: the markers of the agent session the cockpit itself was
    // launched from, the host terminal's self-identification, and any inherited Anthropic credential (which
    // would silently move a session onto API-key billing — see `IsAnthropicCredentialMarker`).
    // A provider adds to this map through `Abstractions.Sessions.TtyLaunchSpec.EnvironmentOverlay`;
    // it cannot take away from it, because a scrub that each provider could opt out of is not a scrub.
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

    // Lays a provider's overlay over the base: a value sets, `null` removes.
    //
    // A provider cannot reinstate what the host stripped. Keys the host owns (`IsHostControlled`)
    // are ignored in an overlay — otherwise the scrub would be advisory, and a provider could hand the child an
    // `ANTHROPIC_API_KEY` that silently moves the session onto API-key billing. Removing them stays
    // allowed, because removing something already absent asks for nothing.
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

    // The keys an overlay tried to set but does not get to (`IsHostControlled`). Pure, so the
    // composition stays testable; the launcher logs them, because a security rule that fires silently is one
    // nobody finds out about until it matters.
    public static IReadOnlyList<string> RejectedOverlayKeys(IReadOnlyDictionary<string, string?> overlay) =>
        [.. overlay.Where(entry => entry.Value is not null && IsHostControlled(entry.Key)).Select(entry => entry.Key)];

    // True for a variable the host decides about, not a provider: the markers of the agent session the cockpit
    // was launched from, the host terminal's self-identification, and any Anthropic credential. These are
    // stripped from the inherited environment and cannot be put back by an overlay.
    public static bool IsHostControlled(string key) =>
        IsNestedClaudeCodeMarker(key)
        || IsHostTerminalIdentityMarker(key)
        || IsAnthropicCredentialMarker(key)
        || IsCockpitMcpKeyMarker(key)
        || IsCockpitPaneIdMarker(key);

    // True when the effective ctype locale is UTF-8. The C library resolves the ctype category as
    // `LC_ALL` (if set) else `LC_CTYPE` else `LANG`, so this checks them in that precedence
    // and treats a value containing `UTF-8`/`UTF8` (case-insensitive) as UTF-8.
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

    // True for the environment variables a running Claude Code session exports to mark itself
    // (`CLAUDECODE`, `CLAUDE_CODE_*`, `CLAUDE_AGENT_*`) — notably
    // `CLAUDE_CODE_SESSION_ID`. Stripped before spawning so a cockpit launched from within such a
    // session does not hand its own session identity down to the child CLI. `CLAUDE_CONFIG_DIR` is
    // deliberately not matched (it does not start with `CLAUDE_CODE`) and is re-applied per profile.
    public static bool IsNestedClaudeCodeMarker(string key) =>
        key.StartsWith("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("CLAUDE_AGENT_", StringComparison.OrdinalIgnoreCase);

    // True for the environment variables a host terminal emulator uses to self-identify to child
    // processes — `TERM_PROGRAM`/`TERM_PROGRAM_VERSION` (set by most modern terminals,
    // including Ghostty, which sets `TERM_PROGRAM=ghostty`) and Ghostty's own `GHOSTTY_*`
    // variables (e.g. `GHOSTTY_RESOURCES_DIR`, `GHOSTTY_BIN_DIR`). Stripped for the same
    // reason `TermValue` pins `TERM` to a generic value: the pty child (`claude`)
    // is actually rendered by Cockpit's own Exclr8 terminal emulator, not by whatever terminal
    // launched Cockpit. If `TERM_PROGRAM=ghostty` leaked through, claude's Ink TUI would detect
    // "running inside Ghostty" and pick a Ghostty-specific render path (advanced escape sequences
    // Ghostty supports) that Exclr8 does not match, causing a vertical render desync (input echo
    // jumping to the top row instead of tracking the cursor).
    // `TERMINFO`/`TERMINFO_DIRS` are deliberately NOT matched here: claude's Ink TUI is a
    // Node.js process that does not consult the ncurses terminfo database for its own rendering
    // decisions, so a Ghostty-pointed `TERMINFO_DIRS` does not reproduce this bug — scrubbing it
    // would only risk breaking an unrelated subprocess that does shell out to a terminfo-aware tool.
    // `COLORTERM` is also deliberately not matched — it is a generic truecolor-support signal
    // (not a terminal-identity marker) and Exclr8 does support truecolor, so it should pass through.
    public static bool IsHostTerminalIdentityMarker(string key) =>
        key.Equals("TERM_PROGRAM", StringComparison.OrdinalIgnoreCase)
        || key.Equals("TERM_PROGRAM_VERSION", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("GHOSTTY_", StringComparison.OrdinalIgnoreCase);

    // True for an Anthropic credential in the environment (`ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`,
    // and the rest of the `ANTHROPIC_*` family). Two reasons to strip them rather than merely not set them:
    // a key that reaches the CLI switches the session from the operator's subscription to API-key billing —
    // silently, and on someone else's invoice — and a credential that the cockpit inherited from whatever
    // launched it has no business being handed on to a child it did not come from. A normal desktop launch has
    // none of these set, so this is a no-op there; it bites exactly when the cockpit is started from a shell
    // that exports one.
    public static bool IsAnthropicCredentialMarker(string key) =>
        key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase);

    // True for `COCKPIT_MCP_KEY`, this run's bearer for the cockpit's own loopback MCP endpoints (AC-40). The
    // host sets it fresh on every spawn, so a session profile must not get to override it — otherwise that session
    // would present the wrong key and lock itself out of the internal endpoints (a self-inflicted 401). Stripping it
    // from the inherited environment too means a cockpit launched from another cockpit session never carries the
    // parent's key down; the child gets its own. The literal name mirrors
    // `WellKnownSessionEnvironment.CockpitMcpKey`, which Core does not reference.
    public static bool IsCockpitMcpKeyMarker(string key) =>
        key.Equals("COCKPIT_MCP_KEY", StringComparison.OrdinalIgnoreCase);

    // True for `COCKPIT_PANE_ID`, the identity the host hands a session so its agent can name itself to the
    // cockpit-session MCP (AC-13). It is who the session *is*, not a setting: a session that could choose its
    // own would be able to set another pane's statusline or claim another pane's consent, so nothing but the host
    // gets to write it. The literal name mirrors `WellKnownSessionEnvironment`'s, which Core does not reference.
    public static bool IsCockpitPaneIdMarker(string key) =>
        key.Equals("COCKPIT_PANE_ID", StringComparison.OrdinalIgnoreCase);
}
