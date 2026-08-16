using Cockpit.Core.Abstractions.Diagrams;

namespace Cockpit.Plugin.Diagram;

// AC-816's quick-start: a working title for the surface and, optionally, a session to couple with zero
// capabilities. Couple, never Grant — read_diagram and edit_diagram still ask their own consent on first
// use (AC-810); a quick-start that called Grant here would silently open that gate.
internal sealed record DiagramQuickStart(string Name, string? SessionPaneId)
{
    public void ApplyTo(IDiagramAccessRegistry registry, string surfaceId, string initialText)
    {
        registry.SurfaceOpened(surfaceId, Name, initialText);
        if (SessionPaneId is { } sessionId)
        {
            registry.Couple(sessionId, surfaceId);
        }
    }
}
