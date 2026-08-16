using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Whiteboard.Rendering;

namespace Cockpit.Plugin.Diagram.Whiteboard.Canvas;

// One template shape or pasted screenshot, drawn in its own local 0,0..Width,Height rect — the canvas positions
// it on the surface with Canvas.SetLeft/SetTop, the same way WorkflowNodeControl is positioned.
internal sealed class PlacedObjectControl : Control
{
    private Bitmap? _image;

    public PlacedObjectControl(PlacedObject model)
    {
        Model = model;
        Width = model.Width;
        Height = model.Height;
        _ReloadImage();
    }

    public PlacedObject Model { get; }

    public void Refresh()
    {
        Width = Model.Width;
        Height = Model.Height;
        _ReloadImage();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        WhiteboardObjectPainter.PaintPlaced(context, Model.ShapeKind, new Rect(Bounds.Size), Model.Text, _image);
    }

    private void _ReloadImage()
    {
        _image?.Dispose();
        _image = Model is { ShapeKind: PlacedShapeKind.Image, ImageData: { Length: > 0 } data }
            ? new Bitmap(new MemoryStream(data))
            : null;
    }
}
