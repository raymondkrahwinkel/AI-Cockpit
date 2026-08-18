namespace Cockpit.Plugin.Diagram.Whiteboard.Model;

// W-6/AC-851: decides which pasted image (if any) a new stroke or placed object lands on, and re-maps everything
// anchored to an image when that image moves or resizes. Objects keep their own absolute X/Y/Points; ParentImageId
// is the only extra state — painting, hit-testing and the snapshot renderer need no changes at all.
public static class WhiteboardBinding
{
    // The topmost image (last drawn wins, same z-order rule WhiteboardCanvasControl already hit-tests freehand
    // strokes with) whose rectangle contains the given point — the anchor a stroke/object centred there belongs to.
    public static PlacedObject? FindParentImage(WhiteboardDocument document, double x, double y)
    {
        foreach (var candidate in document.Objects.OfType<PlacedObject>().Reverse())
        {
            if (candidate.ShapeKind == PlacedShapeKind.Image
                && x >= candidate.X && x <= candidate.X + candidate.Width
                && y >= candidate.Y && y <= candidate.Y + candidate.Height)
            {
                return candidate;
            }
        }

        return null;
    }

    // Every object still anchored to `parentId` — the board's answer to "what happens if I delete this image".
    public static IReadOnlyList<WhiteboardObject> ChildrenOf(WhiteboardDocument document, Guid parentId) =>
        [.. document.Objects.Where(o => o.ParentImageId == parentId)];

    // Carries `parentId`'s children along with the image's own move/resize — translation for a drag, translation+scale
    // for a resize, always derived from the bounds before/after so repeated calls never drift. `only` narrows it to the
    // children one gesture actually carried, which is how AC-912's undo leaves work that landed since alone.
    public static void CarryChildren(
        WhiteboardDocument document,
        Guid parentId,
        double oldX, double oldY, double oldWidth, double oldHeight,
        double newX, double newY, double newWidth, double newHeight,
        IReadOnlyCollection<Guid>? only = null)
    {
        var scaleX = oldWidth > 0 ? newWidth / oldWidth : 1;
        var scaleY = oldHeight > 0 ? newHeight / oldHeight : 1;

        double MapX(double x) => newX + ((x - oldX) * scaleX);
        double MapY(double y) => newY + ((y - oldY) * scaleY);

        foreach (var child in document.Objects.Where(o => o.ParentImageId == parentId && (only is null || only.Contains(o.Id))))
        {
            switch (child)
            {
                case PlacedObject placed:
                    var right = MapX(placed.X + placed.Width);
                    var bottom = MapY(placed.Y + placed.Height);
                    placed.X = MapX(placed.X);
                    placed.Y = MapY(placed.Y);
                    placed.Width = right - placed.X;
                    placed.Height = bottom - placed.Y;
                    break;
                case FreehandStroke stroke:
                    for (var i = 0; i < stroke.Points.Count; i++)
                    {
                        stroke.Points[i] = new WhiteboardPoint(MapX(stroke.Points[i].X), MapY(stroke.Points[i].Y));
                    }

                    break;
            }
        }
    }
}
