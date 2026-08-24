using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Cockpit.App.Controls;

// AC-678: dropping WindowDecorations.BorderOnly (its resize border was the unwanted margin) for None also
// drops the platform's own edge/corner dragging, so every window needs its own now. Split into a pure zone
// calculation and the pointer wiring around it (DialogScreenClamp's shape), so the zones are testable without a window.
internal static class WindowResizeGrip
{
    // How wide the invisible grab band is, in DIPs, on each edge.
    internal const double BorderThickness = 6;

    // Gives a window the way it is resized on this platform: no OS decoration and a grip of our own, or —
    // on macOS/Windows — the platform's own resize border and the margin that comes with it.
    public static void Apply(Window window)
    {
        window.WindowDecorations = DecorationsFor(OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());
        if (window.WindowDecorations == WindowDecorations.None)
        {
            _Attach(window);
        }
    }

    // AC-1013: AC-755 (macOS BeginResizeDrag is a no-op; WindowDecorations.None disables native resize —
    // AvaloniaUI/Avalonia#3834, WindowImpl.mm) and AC-934 (Windows: same None strips WS_CAPTION/
    // THICKFRAME/MAXIMIZEBOX needed for Aero Snap) both need BorderOnly instead; details on the tickets.
    internal static WindowDecorations DecorationsFor(bool isMacOs, bool isWindows = false) =>
        isMacOs || isWindows ? WindowDecorations.BorderOnly : WindowDecorations.None;

    // Wires pointer handling for a window that has lost its own OS resize border. A window that opted out of
    // resizing (CanResize="False" — every SizeToContent dialog) gets neither the cursor nor the drag: there is
    // nothing on its edge to grab.
    private static void _Attach(Window window)
    {
        if (!window.CanResize)
        {
            return;
        }

        // Tunnel, not bubble: this must see the press before the title bar's own drag handler or a caption
        // button does, or a press near the top edge would move the window instead of resizing it. Marking the
        // event handled here is what stops it reaching them.
        window.AddHandler(InputElement.PointerPressedEvent, _OnPressed, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerMovedEvent, _OnMoved, RoutingStrategies.Tunnel);
    }

    private static void _OnMoved(object? sender, PointerEventArgs e)
    {
        // A button's own glyph can sit inside the grab band at a window's corner (the close button, top-right);
        // the button owns its pixels there, so the band does not.
        if (e.Source is Button)
        {
            return;
        }

        var window = (Window)sender!;
        var edge = EdgeAt(window.ClientSize, e.GetPosition(window));
        window.Cursor = edge is { } value ? new Cursor(_CursorFor(value)) : Cursor.Default;
    }

    private static void _OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button)
        {
            return;
        }

        var window = (Window)sender!;
        if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (EdgeAt(window.ClientSize, e.GetPosition(window)) is { } edge)
        {
            e.Handled = true;
            window.BeginResizeDrag(edge, e);
        }
    }

    // Pure: which of the eight resize zones (if any) a point falls in, on a surface of the given size. A corner
    // wins over a plain edge wherever the two bands overlap — a diagonal drag is more useful there than either
    // axis alone in a band this narrow.
    internal static WindowEdge? EdgeAt(Size size, Point point, double thickness = BorderThickness)
    {
        var north = point.Y <= thickness;
        var south = point.Y >= size.Height - thickness;
        var west = point.X <= thickness;
        var east = point.X >= size.Width - thickness;

        if (north && west)
        {
            return WindowEdge.NorthWest;
        }

        if (north && east)
        {
            return WindowEdge.NorthEast;
        }

        if (south && west)
        {
            return WindowEdge.SouthWest;
        }

        if (south && east)
        {
            return WindowEdge.SouthEast;
        }

        if (north)
        {
            return WindowEdge.North;
        }

        if (south)
        {
            return WindowEdge.South;
        }

        if (west)
        {
            return WindowEdge.West;
        }

        if (east)
        {
            return WindowEdge.East;
        }

        return null;
    }

    private static StandardCursorType _CursorFor(WindowEdge edge) => edge switch
    {
        WindowEdge.North or WindowEdge.South => StandardCursorType.SizeNorthSouth,
        WindowEdge.East or WindowEdge.West => StandardCursorType.SizeWestEast,
        WindowEdge.NorthWest => StandardCursorType.TopLeftCorner,
        WindowEdge.NorthEast => StandardCursorType.TopRightCorner,
        WindowEdge.SouthWest => StandardCursorType.BottomLeftCorner,
        WindowEdge.SouthEast => StandardCursorType.BottomRightCorner,
        _ => StandardCursorType.Arrow,
    };
}
