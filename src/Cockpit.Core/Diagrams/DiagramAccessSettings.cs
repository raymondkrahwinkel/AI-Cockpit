namespace Cockpit.Core.Diagrams;

// The master switch for the diagram-access MCP (AC-810): when `Enabled` is false — the default — the
// `cockpit-diagram` endpoint is not advertised to any session, so for an agent the feature does not exist and no
// surface is reachable. Mirrors `Cockpit.Core.Terminal.TerminalAccessSettings` (AC-34).
public sealed record DiagramAccessSettings
{
    public bool Enabled { get; init; }

    public static DiagramAccessSettings Default { get; } = new();
}
