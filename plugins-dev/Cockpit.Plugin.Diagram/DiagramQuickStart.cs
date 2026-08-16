namespace Cockpit.Plugin.Diagram;

// AC-816's quick-start: a working title for the diagram and, optionally, a session that is already running to
// couple it to. Nothing is granted here — read_diagram and edit_diagram still ask their own consent (AC-810).
internal sealed record DiagramQuickStart(string Name, string? SessionPaneId);
