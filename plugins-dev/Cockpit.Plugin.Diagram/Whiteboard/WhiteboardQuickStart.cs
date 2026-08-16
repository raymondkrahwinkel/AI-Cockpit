namespace Cockpit.Plugin.Diagram.Whiteboard;

// AC-843's quick-start: a working title for the board — DiagramQuickStart's counterpart. Nothing is granted here;
// the invite bar still asks its own consent (AC-810).
internal sealed record WhiteboardQuickStart(string Name, string? SessionPaneId);
