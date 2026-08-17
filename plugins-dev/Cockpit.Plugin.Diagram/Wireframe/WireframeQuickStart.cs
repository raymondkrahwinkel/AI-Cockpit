namespace Cockpit.Plugin.Diagram.Wireframe;

// AC-873's quick-start: a working title for the wireframe and, optionally, a session that is already running to
// couple it to. Nothing is granted here — read_wireframe and edit_wireframe still ask their own consent.
internal sealed record WireframeQuickStart(string Name, string? SessionPaneId);
