using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace OwnCanvasSpike;

/// <summary>
/// One node on the canvas: a titled box with pins down its left (inputs) and right (outputs) edge. It is an
/// ordinary Avalonia control, which is the whole point — the framework does the hit-testing, so a pin is a thing
/// you can click because it *is* a control, not because we walk a geometry tree looking for one.
/// </summary>
internal sealed class FlowNode : Border
{
    private readonly StackPanel _inputs = new() { Spacing = 6 };
    private readonly StackPanel _outputs = new() { Spacing = 6 };

    public event EventHandler<PointerPressedEventArgs>? HeaderPressed;

    public event EventHandler<FlowPin>? PinPressed;

    public event EventHandler<FlowPin>? PinReleased;

    public FlowNode(string title, int inputs, int outputs)
    {
        Width = 200;
        Background = new SolidColorBrush(Color.Parse("#2A2A31"));
        BorderBrush = new SolidColorBrush(Color.Parse("#3C3C46"));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);

        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#33333C")),
            CornerRadius = new CornerRadius(5, 5, 0, 0),
            Padding = new Thickness(10, 6),
            Child = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = Brushes.White },
        };
        header.PointerPressed += (_, e) => HeaderPressed?.Invoke(this, e);

        for (var index = 0; index < inputs; index++)
        {
            _inputs.Children.Add(_BuildPin(isInput: true));
        }

        for (var index = 0; index < outputs; index++)
        {
            _outputs.Children.Add(_BuildPin(isInput: false));
        }

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 8, 0, 10),
        };
        Grid.SetColumn(_inputs, 0);
        Grid.SetColumn(_outputs, 2);
        body.Children.Add(_inputs);
        body.Children.Add(_outputs);

        Child = new StackPanel { Children = { header, body } };
    }

    public IEnumerable<FlowPin> Pins => _inputs.Children.Concat(_outputs.Children).OfType<FlowPin>();

    private FlowPin _BuildPin(bool isInput)
    {
        var pin = new FlowPin(this, isInput)
        {
            HorizontalAlignment = isInput ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = isInput ? new Thickness(-6, 0, 0, 0) : new Thickness(0, 0, -6, 0),
        };

        pin.PointerPressed += (_, e) =>
        {
            PinPressed?.Invoke(this, pin);
            e.Handled = true;
        };
        pin.PointerReleased += (_, _) => PinReleased?.Invoke(this, pin);

        return pin;
    }
}

/// <summary>A connection point on a node — a small circle you can drag a wire out of, or drop one onto.</summary>
internal sealed class FlowPin : Ellipse
{
    public FlowPin(FlowNode owner, bool isInput)
    {
        Owner = owner;
        IsInput = isInput;
        Width = 12;
        Height = 12;
        Fill = new SolidColorBrush(Color.Parse(isInput ? "#6E9FEA" : "#E4A055"));
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public FlowNode Owner { get; }

    public bool IsInput { get; }

    /// <summary>Where the wire attaches, in the canvas's own coordinates — the pin moves with its node, so this is asked fresh every redraw.</summary>
    public Point AnchorOn(Visual surface)
    {
        var origin = this.TranslatePoint(new Point(Width / 2, Height / 2), surface);
        return origin ?? default;
    }
}
