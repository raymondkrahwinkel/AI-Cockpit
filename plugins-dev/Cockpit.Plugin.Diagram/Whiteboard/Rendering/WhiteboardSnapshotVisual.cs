using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Whiteboard.Rendering;

// Paints straight from the document, never from a live control tree — so it renders the same whether or not any
// window showing the board actually exists.
internal sealed class WhiteboardSnapshotVisual : Control
{
    private readonly WhiteboardDocument _document;

    public WhiteboardSnapshotVisual(WhiteboardDocument document)
    {
        _document = document;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));

        foreach (var item in _document.Objects)
        {
            switch (item)
            {
                case FreehandStroke stroke:
                    WhiteboardObjectPainter.PaintFreehand(context, stroke.Points, stroke.Thickness, stroke.IsMarker);
                    break;
                case PlacedObject placed:
                    // ponytail: decodes the pasted image on every snapshot, no bitmap cache — add one if AC-822's
                    // panel starts re-rendering fast enough for that to matter.
                    var image = placed is { ShapeKind: PlacedShapeKind.Image, ImageData: { Length: > 0 } data }
                        ? new Bitmap(new MemoryStream(data))
                        : null;
                    using (image)
                    {
                        WhiteboardObjectPainter.PaintPlaced(
                            context,
                            placed.ShapeKind,
                            new Rect(placed.X, placed.Y, placed.Width, placed.Height),
                            placed.Text,
                            image,
                            WhiteboardObjectPainter.BadgeFor(placed));
                    }

                    break;
            }
        }
    }
}
