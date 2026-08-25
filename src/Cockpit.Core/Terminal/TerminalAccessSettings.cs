namespace Cockpit.Core.Terminal;

// The master switch for the terminal-access MCP (AC-34): when `Enabled` is false — the default — the
// `cockpit-terminal` endpoint is not advertised to any session, so for an agent the feature does not exist.
// Turning it on is opt-in; only then does the per-pane Approve/Deny gate come into play.
public sealed record TerminalAccessSettings
{
    public bool Enabled { get; init; }

    public static TerminalAccessSettings Default { get; } = new();
}
