using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// One step on the canvas (#69), in the shape that makes a flow readable at a glance: a square tile carrying the
/// type's icon, the operator's name for it underneath, and the type itself under that in small print. What the
/// step <em>is</em> you read from the icon; what it is <em>for</em> from the name you gave it.
/// <para>
/// A trigger is rounded on its left and wears a lightning bolt, so where a flow begins is visible without reading
/// a word — the one piece of n8n's visual language worth copying outright, because it works.
/// </para>
/// </summary>
internal sealed class WorkflowNodeControl : Border
{
    public const double TileSize = 88;

    private readonly List<WorkflowPin> _outputs = [];
    private readonly WorkflowPin? _input;
    private readonly Border _tile;

    public WorkflowNodeControl(WorkflowNode node)
    {
        Node = node;
        Background = Brushes.Transparent;
        Width = 150;

        var isTrigger = node.Kind == WorkflowNodeKind.Trigger;

        _tile = new Border
        {
            Width = TileSize,
            Height = TileSize,
            Background = _Brush("CockpitPanelBgBrush") ?? new SolidColorBrush(Color.Parse("#2A2A31")),
            BorderBrush = _Hairline,
            BorderThickness = new Thickness(1),
            // A trigger's left side is round: a flow visibly starts somewhere rather than just happening to have
            // a leftmost box.
            CornerRadius = isTrigger ? new CornerRadius(TileSize / 2, 8, 8, TileSize / 2) : new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = node.Type?.Icon ?? "?",
                FontSize = 30,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _tile.PointerPressed += (_, e) => HeaderPressed?.Invoke(this, e);
        _tile.Cursor = new Cursor(StandardCursorType.SizeAll);
        ToolTip.SetTip(_tile, node.Type?.Description ?? $"This flow uses '{node.TypeId}', which this cockpit does not have — a plugin may be missing.");

        var name = new TextBlock
        {
            Text = node.Name,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var subtitle = new TextBlock
        {
            Text = node.Type?.Name ?? node.TypeId,
            FontSize = 9,
            Opacity = 0.55,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        // The lightning bolt sits outside the tile, to its left, exactly where a run enters the flow.
        var bolt = new TextBlock
        {
            Text = "⚡",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            IsVisible = isTrigger,
        };

        var inputColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (node.HasInput)
        {
            _input = _BuildPin(isInput: true, outputIndex: -1);
            inputColumn.Children.Add(_input);
        }

        var outputColumn = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        for (var index = 0; index < node.Outputs.Count; index++)
        {
            var pin = _BuildPin(isInput: false, outputIndex: index);
            _outputs.Add(pin);
            outputColumn.Children.Add(pin);
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { bolt, inputColumn, _tile, outputColumn },
        };

        Child = new StackPanel
        {
            Opacity = node.IsDisabled ? 0.4 : 1,
            Children = { row, name, subtitle },
        };
    }

    public WorkflowNode Node { get; }

    public event EventHandler<PointerPressedEventArgs>? HeaderPressed;

    public event EventHandler<WorkflowPin>? PinPressed;

    public event EventHandler<WorkflowPin>? PinReleased;

    /// <summary>Selection is a ring around the tile, not a colour change — the tile's own colour already says what kind of step it is.</summary>
    public bool IsSelected
    {
        set
        {
            _tile.BorderBrush = value ? _Brush("CockpitAccentBrush") ?? Brushes.Orange : _Hairline;
            _tile.BorderThickness = new Thickness(value ? 2 : 1);
        }
    }

    public WorkflowPin OutputPin(int index) => _outputs[Math.Clamp(index, 0, _outputs.Count - 1)];

    public WorkflowPin InputPin() => _input ?? _outputs[0];

    public IReadOnlyList<WorkflowPin> OutputPins => _outputs;

    private WorkflowPin _BuildPin(bool isInput, int outputIndex)
    {
        var pin = new WorkflowPin(this, isInput, outputIndex)
        {
            Margin = isInput ? new Thickness(0, 0, -5, 0) : new Thickness(-5, 0, 0, 0),
        };

        pin.PointerPressed += (_, e) =>
        {
            PinPressed?.Invoke(this, pin);
            e.Handled = true;
        };
        pin.PointerReleased += (_, _) => PinReleased?.Invoke(this, pin);

        return pin;
    }

    private static IBrush _Hairline => _Brush("CockpitHairlineBrush") ?? new SolidColorBrush(Color.Parse("#3C3C46"));

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}

/// <summary>A connection point: pull a wire out of an output, drop it on an input.</summary>
internal sealed class WorkflowPin : Ellipse
{
    public WorkflowPin(WorkflowNodeControl owner, bool isInput, int outputIndex)
    {
        Owner = owner;
        IsInput = isInput;
        OutputIndex = outputIndex;

        Width = 10;
        Height = 10;
        Fill = new SolidColorBrush(Color.Parse("#8A8A99"));
        Cursor = new Cursor(StandardCursorType.Hand);

        var label = isInput ? null : owner.Node.Outputs.ElementAtOrDefault(outputIndex);
        ToolTip.SetTip(this, isInput
            ? "Where this step's input comes in"
            : string.IsNullOrEmpty(label)
                ? "Drag from here to the next step"
                : $"The '{label}' branch — drag from here to the next step");
    }

    public WorkflowNodeControl Owner { get; }

    public bool IsInput { get; }

    /// <summary>Which way out this is (a decision has two); -1 for an input.</summary>
    public int OutputIndex { get; }

    /// <summary>Where the wire attaches, in canvas coordinates — asked fresh on every redraw, because the pin moves with its node.</summary>
    public Point AnchorOn(Visual surface) => this.TranslatePoint(new Point(Width / 2, Height / 2), surface) ?? default;
}
