namespace Cockpit.Core.Terminal;

/// <summary>
/// The master switch for the terminal-access MCP (AC-34): when <see cref="Enabled"/> is false — the default — the
/// <c>cockpit-terminal</c> endpoint is not advertised to any session, so for an agent the feature does not exist and
/// no pane is reachable. Turning it on is a deliberate, opt-in choice, consistent with "default not accessible": only
/// then do the per-pane Approve/Deny gate and the coupling lifecycle come into play.
/// </summary>
public sealed record TerminalAccessSettings
{
    public bool Enabled { get; init; }

    public static TerminalAccessSettings Default { get; } = new();
}
