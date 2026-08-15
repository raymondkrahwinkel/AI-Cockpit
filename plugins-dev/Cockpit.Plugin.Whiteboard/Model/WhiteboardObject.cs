namespace Cockpit.Plugin.Whiteboard.Model;

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
}
