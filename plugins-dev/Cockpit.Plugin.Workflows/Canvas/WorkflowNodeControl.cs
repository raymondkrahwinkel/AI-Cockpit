using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// One node on the canvas: a titled box with its input pin on the left and its ways out on the right, labelled
/// where a label means something (a decision's "yes" and "no"). It is an ordinary control, which is why the
/// canvas needs no hit-testing of its own — a pin is clickable because it <em>is</em> a control.
/// </summary>
internal sealed class WorkflowNodeControl : Border
{
    private const double NodeWidth = 190;

    private readonly List<WorkflowPin> _outputs = [];
    private readonly WorkflowPin? _input;

    public WorkflowNodeControl(WorkflowNode node)
    {
        Node = node;

        Width = NodeWidth;
        Background = _Brush("CockpitPanelBgBrush") ?? new SolidColorBrush(Color.Parse("#2A2A31"));
        BorderBrush = _Brush("CockpitHairlineBrush") ?? new SolidColorBrush(Color.Parse("#3C3C46"));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);

        var header = new Border
        {
            Background = _KindBrush(node.Kind),
            CornerRadius = new CornerRadius(5, 5, 0, 0),
            Padding = new Thickness(10, 5),
            Child = new TextBlock
            {
                Text = node.Title,
                FontWeight = FontWeight.SemiBold,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        header.PointerPressed += (_, e) => HeaderPressed?.Invoke(this, e);
        header.Cursor = new Cursor(StandardCursorType.SizeAll);

        var inputColumn = new StackPanel { Spacing = 6 };
        if (node.HasInput)
        {
            _input = _BuildPin(isInput: true, outputIndex: -1);
            inputColumn.Children.Add(_input);
        }

        var outputColumn = new StackPanel { Spacing = 6 };
        for (var index = 0; index < node.OutputCount; index++)
        {
            var pin = _BuildPin(isInput: false, outputIndex: index);
            _outputs.Add(pin);

            var label = node.OutputLabels.ElementAtOrDefault(index) ?? string.Empty;
            outputColumn.Children.Add(label.Length == 0
                ? pin
                : new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children =
                    {
                        new TextBlock { Text = label, FontSize = 10, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                        pin,
                    },
                });
        }

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 8, 0, 10) };
        Grid.SetColumn(inputColumn, 0);
        Grid.SetColumn(outputColumn, 2);
        body.Children.Add(inputColumn);
        body.Children.Add(outputColumn);

        var subtitle = new TextBlock
        {
            Text = node.TypeId,
            FontSize = 9,
            Opacity = 0.55,
            Margin = new Thickness(10, 0, 10, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Child = new StackPanel { Children = { header, subtitle, body } };
    }

    public WorkflowNode Node { get; }

    public event EventHandler<PointerPressedEventArgs>? HeaderPressed;

    public event EventHandler<WorkflowPin>? PinPressed;

    public event EventHandler<WorkflowPin>? PinReleased;

    public bool IsSelected
    {
        set => BorderBrush = value
            ? _Brush("CockpitAccentBrush") ?? Brushes.Orange
            : _Brush("CockpitHairlineBrush") ?? new SolidColorBrush(Color.Parse("#3C3C46"));
    }

    public WorkflowPin OutputPin(int index) => _outputs[Math.Clamp(index, 0, _outputs.Count - 1)];

    public WorkflowPin InputPin() => _input ?? _outputs[0];

    private WorkflowPin _BuildPin(bool isInput, int outputIndex)
    {
        var pin = new WorkflowPin(this, isInput, outputIndex)
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

    // A trigger, an action and a decision read differently at a glance, which is the only thing colour is for here.
    private static IBrush _KindBrush(WorkflowNodeKind kind) => kind switch
    {
        WorkflowNodeKind.Trigger => new SolidColorBrush(Color.Parse("#3E5C3A")),
        WorkflowNodeKind.Decision => new SolidColorBrush(Color.Parse("#5C4A33")),
        _ => new SolidColorBrush(Color.Parse("#33333C")),
    };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}

/// <summary>A connection point: a small circle you pull a wire out of (an output) or drop one onto (an input).</summary>
internal sealed class WorkflowPin : Ellipse
{
    public WorkflowPin(WorkflowNodeControl owner, bool isInput, int outputIndex)
    {
        Owner = owner;
        IsInput = isInput;
        OutputIndex = outputIndex;

        Width = 11;
        Height = 11;
        Fill = new SolidColorBrush(Color.Parse(isInput ? "#6E9FEA" : "#E4A055"));
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, isInput ? "Where the flow comes in" : "Drag a wire from here to the next step");
    }

    public WorkflowNodeControl Owner { get; }

    public bool IsInput { get; }

    /// <summary>Which way out this is (a decision has two); -1 for an input.</summary>
    public int OutputIndex { get; }

    /// <summary>Where the wire attaches, in the canvas's coordinates — asked fresh on every redraw, because the pin moves with its node.</summary>
    public Point AnchorOn(Visual surface) => this.TranslatePoint(new Point(Width / 2, Height / 2), surface) ?? default;
}
