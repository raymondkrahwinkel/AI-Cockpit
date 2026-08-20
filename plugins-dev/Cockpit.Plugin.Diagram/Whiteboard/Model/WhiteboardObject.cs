namespace Cockpit.Plugin.Diagram.Whiteboard.Model;

// What made the object, not how it happens to be coloured — AC-823's MCP surface reads this field, never a brush.
public enum WhiteboardObjectKind
{
    Freehand,
    Placed,
}

public abstract class WhiteboardObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public abstract WhiteboardObjectKind Kind { get; }

    // W-6/AC-851: the id of the pasted-image PlacedObject this object lies on, or null when it stands on the
    // canvas itself. X/Y/Points stay absolute either way — WhiteboardBinding is the only place that reads this.
    public Guid? ParentImageId { get; set; }

    // AC-916: null = the fixed default for this kind (WhiteboardObjectPainter) — a board saved before this
    // shipped reads in with null and renders exactly as it did. Ignored for StickyNote and Image (WhiteboardObjectPainter).
    public string? Color { get; set; }
}
