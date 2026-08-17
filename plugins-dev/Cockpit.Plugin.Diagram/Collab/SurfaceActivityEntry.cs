namespace Cockpit.Plugin.Diagram.Collab;

// One journal row, shaped for ActivityStrip regardless of whether it came from a DiagramHistoryEntry or a
// WhiteboardHistoryEntry. `ObjectKey` is the strip's jump-to-object convention; `CanRevert` carries each registry's
// own rule for what can still be undone (whiteboard's Erase cannot, see WhiteboardHistoryKind).
internal sealed record SurfaceActivityEntry(string Id, string Origin, string Summary, string? ObjectKey, DateTime When, bool Reverted, bool CanRevert);
