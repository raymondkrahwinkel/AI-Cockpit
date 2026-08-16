namespace Cockpit.Core.Whiteboard;

// The master switch for the whiteboard-access MCP (AC-823): when `Enabled` is false — the default — the
// `cockpit-whiteboard` endpoint is not advertised to any session, so for an agent the feature does not exist and no
// surface is reachable. Mirrors `Cockpit.Core.Diagrams.DiagramAccessSettings` (AC-810).
public sealed record WhiteboardAccessSettings
{
    public bool Enabled { get; init; }

    public static WhiteboardAccessSettings Default { get; } = new();
}
