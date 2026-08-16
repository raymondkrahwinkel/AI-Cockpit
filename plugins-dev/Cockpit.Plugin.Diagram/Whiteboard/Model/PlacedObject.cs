namespace Cockpit.Plugin.Diagram.Whiteboard.Model;

// The toolbar's flyout offers the first eight; StickyNote has its own toolbar icon instead; Image is never chosen
// there — it is what a clipboard paste or an inserted file creates.
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
    StickyNote,
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

    // PNG bytes for a pasted screenshot or an inserted file (ShapeKind.Image); null for every template shape.
    public byte[]? ImageData { get; init; }

    // Only true for a clipboard paste — drives the "geplakt · screenshot" badge, never set for a file insert.
    public bool IsPastedScreenshot { get; init; }
}
