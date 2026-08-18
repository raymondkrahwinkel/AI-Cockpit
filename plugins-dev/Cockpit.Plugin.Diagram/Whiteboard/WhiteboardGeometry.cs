using Avalonia;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Whiteboard;

// AC-913: the whiteboard is a fixed, larger-than-the-window canvas rather than a literally unbounded one — that
// keeps "passend maken" and the agent's snapshot a matter of fitting one known rectangle. Its 4:3 ratio matches
// WhiteboardWorkspaceBody.SnapshotSize on purpose, so the whole-board snapshot never letterboxes at that size.
internal static class WhiteboardGeometry
{
    public static readonly Size WorkspaceSize = new(2400, 1800);

    private const double Margin = 40;

    // Everything drawn or placed, with a margin — or the empty workspace itself when there is nothing yet, so a
    // blank board still fits something sensible instead of zooming in on nothing (AC7).
    public static Rect ContentBounds(WhiteboardDocument document)
    {
        if (document.Objects.Count == 0)
        {
            return new Rect(0, 0, WorkspaceSize.Width, WorkspaceSize.Height);
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var bounds in document.Objects.Select(_BoundsOf))
        {
            minX = Math.Min(minX, bounds.X);
            minY = Math.Min(minY, bounds.Y);
            maxX = Math.Max(maxX, bounds.Right);
            maxY = Math.Max(maxY, bounds.Bottom);
        }

        return new Rect(minX - Margin, minY - Margin, (maxX - minX) + (Margin * 2), (maxY - minY) + (Margin * 2));
    }

    // The uniform scale + offset that fits `content` centred into `target` — same DiagramZoomMath shape the
    // diagram/wireframe surfaces already use (AC-837), reused here for the canvas' own "Fit" and for the agent's
    // snapshot, rather than a second copy of the same arithmetic.
    public static Matrix FitTransform(Rect content, Size target)
    {
        var zoom = DiagramZoomMath.FitZoom(target, content.Size, 0.001, 1000);
        if (zoom <= 0)
        {
            zoom = 1;
        }

        var centered = DiagramZoomMath.CenteredPanOffset(target, content.Size, zoom);
        var pan = centered - (new Vector(content.X, content.Y) * zoom);
        return new Matrix(zoom, 0, 0, zoom, pan.X, pan.Y);
    }

    private static Rect _BoundsOf(WhiteboardObject obj) => obj switch
    {
        PlacedObject p => new Rect(p.X, p.Y, p.Width, p.Height),
        FreehandStroke f => _StrokeBounds(f.Points),
        _ => default,
    };

    private static Rect _StrokeBounds(IReadOnlyList<WhiteboardPoint> points)
    {
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }
}
