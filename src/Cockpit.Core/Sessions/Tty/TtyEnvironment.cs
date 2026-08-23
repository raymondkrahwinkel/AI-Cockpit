namespace Cockpit.Core.Sessions.Tty;

// Composes the environment block for any CLI spawned inside a pseudo console (TTY mode): the host's base
// (`BuildBase`) plus a provider's overlay (`Compose`). Pure so the rules are unit-testable. Unlike the SDK-mode
// spawn, a ConPTY child inherits nothing implicitly, so the base must start from the parent's own variables.
public static class TtyEnvironment
{
    // Value for `TERM`: an xterm-256color pseudo-terminal is what makes `claude`'s Ink
    // TUI see `isTTY=true` and render the interactive interface instead of crashing with
    // "Raw mode is not supported" (the non-TTY/piping artefact).
    public const string TermValue = "xterm-256color";

    // UTF-8 locale forced onto the pty child when the inherited one is missing UTF-8 — Claude's Ink TUI measures
    // glyph widths via `wcwidth`, which miscounts under a non-UTF-8 `LC_CTYPE` and desyncs the layout.
    // `C.UTF-8` is locale-data free (always available on modern glibc/Fedora), a safe universal fallback.
    public const string Utf8LocaleValue = "C.UTF-8";

    // The environment every pty child starts from: the inherited parent environment minus what must not be
    // handed down (a host rule, not a provider's — see `IsAnthropicCredentialMarker`), plus `TERM` and a UTF-8
    // locale. A provider adds via `TtyLaunchSpec.EnvironmentOverlay` but cannot opt out of the scrub.
    public static IReadOnlyDictionary<string, string> BuildBase(IReadOnlyDictionary<string, string> parentEnvironment)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in parentEnvironment)
        {
            // What the host owns is never handed down — an inherited CLAUDE_CODE_SESSION_ID would make the child
            // write into the parent's transcript; a leaked Anthropic credential would move billing to API-key.
            // A normal desktop launch has none of these; it bites only when the cockpit is started from a shell that exports one.
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

    // Lays a provider's overlay over the base: a value sets, `null` removes. A provider cannot reinstate what
    // the host stripped — host-owned keys are ignored in an overlay, else the scrub would be merely advisory.
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

    // True for the env vars a running Claude Code session exports to mark itself (`CLAUDECODE`, `CLAUDE_CODE_*`,
    // `CLAUDE_AGENT_*`), stripped so a cockpit launched from within one doesn't hand its identity to the child.
    // `CLAUDE_CONFIG_DIR` is deliberately not matched here and is re-applied per profile instead.
    public static bool IsNestedClaudeCodeMarker(string key) =>
        key.StartsWith("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("CLAUDE_AGENT_", StringComparison.OrdinalIgnoreCase);

    // True for env vars a host terminal emulator uses to self-identify (`TERM_PROGRAM`/`_VERSION`, Ghostty's
    // `GHOSTTY_*`) — stripped since a leaked `TERM_PROGRAM=ghostty` makes claude's Ink TUI pick a render path
    // Cockpit's own Exclr8 doesn't match (vertical desync). `TERMINFO*`/`COLORTERM` deliberately not matched.
    public static bool IsHostTerminalIdentityMarker(string key) =>
        key.Equals("TERM_PROGRAM", StringComparison.OrdinalIgnoreCase)
        || key.Equals("TERM_PROGRAM_VERSION", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("GHOSTTY_", StringComparison.OrdinalIgnoreCase);

    // True for an Anthropic credential (`ANTHROPIC_*`). Stripped, not merely left unset: a leaked key silently
    // switches the session to API-key billing on someone else's invoice. A no-op except when the cockpit is
    // itself started from a shell that exports one.
    public static bool IsAnthropicCredentialMarker(string key) =>
        key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase);

    // True for `COCKPIT_MCP_KEY` (AC-40), this run's bearer for the cockpit's own loopback MCP endpoints. Set
    // fresh on every spawn, never overridable by a profile — else a session would present the wrong key and
    // self-lock-out (401). Stripped from inheritance too, so a nested cockpit gets its own, never the parent's.
    public static bool IsCockpitMcpKeyMarker(string key) =>
        key.Equals("COCKPIT_MCP_KEY", StringComparison.OrdinalIgnoreCase);

    // True for `COCKPIT_PANE_ID` (AC-13), the identity the host hands a session — who it *is*, not a setting.
    // A session that could choose its own could set another pane's statusline or claim its consent.
    public static bool IsCockpitPaneIdMarker(string key) =>
        key.Equals("COCKPIT_PANE_ID", StringComparison.OrdinalIgnoreCase);
}
