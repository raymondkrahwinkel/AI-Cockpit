using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Cockpit.Plugin.Browser.UI;

// The picture of the agent tab on screen (AC-1713). Shows the last screencast frame; while Raymond drives, his mouse
// and keys go to the page through the model, mapped from this control onto the page's own pixels.
internal sealed class AgentScreen : Control
{
    private static readonly Dictionary<Key, (string Key, int Code)> SpecialKeys = new()
    {
        [Key.Enter] = ("Enter", 13),
        [Key.Back] = ("Backspace", 8),
        [Key.Tab] = ("Tab", 9),
        [Key.Escape] = ("Escape", 27),
        [Key.Delete] = ("Delete", 46),
        [Key.Left] = ("ArrowLeft", 37),
        [Key.Up] = ("ArrowUp", 38),
        [Key.Right] = ("ArrowRight", 39),
        [Key.Down] = ("ArrowDown", 40),
        [Key.Home] = ("Home", 36),
        [Key.End] = ("End", 35),
        [Key.PageUp] = ("PageUp", 33),
        [Key.PageDown] = ("PageDown", 34),
    };

    private readonly AgentTabsModel _model;
    private bool _pressed;

    public AgentScreen(AgentTabsModel model)
    {
        _model = model;
        Focusable = true;
        ClipToBounds = true;
        model.FrameChanged += InvalidateVisual;
    }

    // Pointer and keys are only taken while Raymond drives that tab.
    private bool Driving => _model.Active?.OperatorDrives ?? false;

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (_model.Frame is { } frame && Shown() is { } shown)
        {
            context.DrawImage(frame, new Rect(frame.Size), shown);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Driving == false)
        {
            return;
        }

        Focus();
        _pressed = true;
        Mouse("mousePressed", e, button: Button(e), clickCount: e.ClickCount);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Driving && _pressed)
        {
            _pressed = false;
            Mouse("mouseReleased", e, button: e.InitialPressMouseButton switch { MouseButton.Right => "right", MouseButton.Middle => "middle", _ => "left" }, clickCount: 1);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Driving)
        {
            Mouse("mouseMoved", e, button: _pressed ? "left" : "none", clickCount: 0);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Driving && Page(e.GetPosition(this)) is { } point)
        {
            _ = _model.SendInputAsync("Input.dispatchMouseEvent", new { type = "mouseWheel", x = point.X, y = point.Y, deltaX = -e.Delta.X * 100, deltaY = -e.Delta.Y * 100 });
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Driving && SpecialKeys.TryGetValue(e.Key, out var special))
        {
            _ = _model.SendInputAsync("Input.dispatchKeyEvent", new { type = "rawKeyDown", key = special.Key, windowsVirtualKeyCode = special.Code });
            if (e.Key == Key.Enter)
            {
                _ = _model.SendInputAsync("Input.dispatchKeyEvent", new { type = "char", text = "\r" });
            }

            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (Driving && SpecialKeys.TryGetValue(e.Key, out var special))
        {
            _ = _model.SendInputAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = special.Key, windowsVirtualKeyCode = special.Code });
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (Driving && string.IsNullOrEmpty(e.Text) == false)
        {
            _ = _model.SendInputAsync("Input.dispatchKeyEvent", new { type = "char", text = e.Text });
            e.Handled = true;
        }
    }

    private void Mouse(string type, PointerEventArgs e, string button, int clickCount)
    {
        if (Page(e.GetPosition(this)) is { } point)
        {
            _ = _model.SendInputAsync("Input.dispatchMouseEvent", new { type, x = point.X, y = point.Y, button, clickCount });
        }
    }

    private static string Button(PointerPressedEventArgs e) => e.GetCurrentPoint(null).Properties.PointerUpdateKind switch
    {
        PointerUpdateKind.RightButtonPressed => "right",
        PointerUpdateKind.MiddleButtonPressed => "middle",
        _ => "left",
    };

    // The frame is drawn uniformly into this control, left-aligned at the top, as a browser draws its page.
    private Rect? Shown()
    {
        if (_model.Frame is not { } frame || frame.Size.Width <= 0 || Bounds.Width <= 0)
        {
            return null;
        }

        var scale = Math.Min(Bounds.Width / frame.Size.Width, Bounds.Height / frame.Size.Height);
        return new Rect(0, 0, frame.Size.Width * scale, frame.Size.Height * scale);
    }

    // A position in this control as a point in the page's CSS pixels, or null outside the picture.
    private Point? Page(Point position)
    {
        var (width, height) = _model.PageSize;
        if (Shown() is not { } shown || shown.Contains(position) == false || width <= 0)
        {
            return null;
        }

        return new Point(position.X / shown.Width * width, position.Y / shown.Height * height);
    }
}
