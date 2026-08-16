namespace Cockpit.Plugin.Diagram.Whiteboard.Model;

// The pointer path as drawn, never reshaped after the fact — unlike a PlacedObject it has no handles.
public sealed class FreehandStroke : WhiteboardObject
{
    public override WhiteboardObjectKind Kind => WhiteboardObjectKind.Freehand;

    public List<WhiteboardPoint> Points { get; init; } = [];

    public double Thickness { get; init; } = 2.5;
}
