namespace Cockpit.Plugin.Diagram.Whiteboard.Model;

// No Avalonia.Point here on purpose: the model stays plain data, readable by AC-823 without an Avalonia reference.
public readonly record struct WhiteboardPoint(double X, double Y);
