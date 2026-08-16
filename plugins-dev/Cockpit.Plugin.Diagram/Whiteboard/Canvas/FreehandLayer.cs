using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Whiteboard.Rendering;

namespace Cockpit.Plugin.Diagram.Whiteboard.Canvas;

// Every freehand stroke drawn in one pass, in the surface's own absolute coordinates — a stroke has no bounding
// box worth positioning a control at, unlike a PlacedObject. Selection is asked of the canvas, not hit-tested here.
internal sealed class FreehandLayer : Control
{
    private readonly WhiteboardDocument _document;

    public FreehandLayer(WhiteboardDocument document)
    {
        _document = document;
        IsHitTestVisible = false;
    }

    public Guid? SelectedId { get; set; }

    public override void Render(DrawingContext context)
    {
        foreach (var stroke in _document.Objects.OfType<FreehandStroke>())
        {
            WhiteboardObjectPainter.PaintFreehand(context, stroke.Points, stroke.Thickness, stroke.IsMarker);

            if (stroke.Id == SelectedId)
            {
                var pen = new Pen(Brushes.White, 1, dashStyle: DashStyle.Dash);
                context.DrawRectangle(null, pen, _Bounds(stroke.Points).Inflate(4));
            }
        }
    }

    private static Rect _Bounds(IReadOnlyList<WhiteboardPoint> points)
    {
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }
}
