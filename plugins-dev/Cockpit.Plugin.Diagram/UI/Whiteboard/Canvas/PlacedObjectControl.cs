using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Whiteboard.Rendering;

namespace Cockpit.Plugin.Diagram.Whiteboard.Canvas;

// One template shape or pasted screenshot, drawn in its own local 0,0..Width,Height rect — the canvas positions
// it on the surface with Canvas.SetLeft/SetTop, the same way WorkflowNodeControl is positioned.
internal sealed class PlacedObjectControl : Control, ICustomHitTest
{
    private Bitmap? _image;
    private bool _isSelected;

    public PlacedObjectControl(PlacedObject model)
    {
        Model = model;
        Width = model.Width;
        Height = model.Height;
        _ReloadImage();

        if (WhiteboardObjectPainter.BadgeFor(model) is { } badge)
        {
            ToolTip.SetTip(this, badge.Tooltip);
        }
    }

    public PlacedObject Model { get; }

    // AC-918: the badge is hidden by default, shown on hover (native IsPointerOver) or selection — set by the
    // canvas whenever the selected object changes.
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            InvalidateVisual();
        }
    }

    // Avalonia's default hit test for a plain Control checks actual rendered pixels — a hollow shape (no fill,
    // just an outline) would then miss clicks anywhere in its own middle. The whole bounds count as the object.
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public void Refresh()
    {
        Width = Model.Width;
        Height = Model.Height;
        _ReloadImage();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        WhiteboardObjectPainter.PaintPlaced(
            context,
            Model.ShapeKind,
            new Rect(Bounds.Size),
            Model.Text,
            _image,
            Model.Color,
            WhiteboardObjectPainter.BadgeFor(Model),
            IsPointerOver || IsSelected);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        InvalidateVisual();
    }

    private void _ReloadImage()
    {
        _image?.Dispose();
        _image = Model is { ShapeKind: PlacedShapeKind.Image, ImageData: { Length: > 0 } data }
            ? new Bitmap(new MemoryStream(data))
            : null;
    }
}
