using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Cockpit.Plugin.Whiteboard.Rendering;

namespace Cockpit.Plugin.Whiteboard.Canvas;

internal enum HandleCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

// A small square grip at one corner of a selected PlacedObject. Not a Button, for the same reason PlusHandle in
// the workflow canvas is not one: a Button marks the press handled before a drag can start on it.
internal sealed class ResizeHandle : Border
{
    private const double Size = 10;

    public ResizeHandle(HandleCorner corner)
    {
        Corner = corner;
        Width = Size;
        Height = Size;
        Background = new SolidColorBrush(WhiteboardObjectPainter.PlacedColor);
        BorderBrush = Brushes.White;
        BorderThickness = new Thickness(1);
        Cursor = new Cursor(corner is HandleCorner.TopLeft or HandleCorner.BottomRight
            ? StandardCursorType.TopLeftCorner
            : StandardCursorType.TopRightCorner);
    }

    public HandleCorner Corner { get; }

    public event EventHandler<PointerPressedEventArgs>? Pressed;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Pressed?.Invoke(this, e);
        e.Handled = true;
    }
}
