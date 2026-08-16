namespace Cockpit.Plugin.Diagram.Whiteboard.Model;

// The toolbar's flyout offers the first eight; Image is never chosen there — it is what a clipboard paste creates.
public enum PlacedShapeKind
{
    Rectangle,
    RoundedRectangle,
    Ellipse,
    Diamond,
    Arrow,
    Column,
    Callout,
    Text,
    Image,
}

// A template shape or a pasted screenshot: both move/resize/delete by the same handles, never freehand-drawn over.
public sealed class PlacedObject : WhiteboardObject
{
    public override WhiteboardObjectKind Kind => WhiteboardObjectKind.Placed;

    public required PlacedShapeKind ShapeKind { get; init; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 120;
    public double Height { get; set; } = 80;

    public string? Text { get; set; }

    // PNG bytes for a pasted screenshot (ShapeKind.Image); null for every template shape.
    public byte[]? ImageData { get; init; }
}
