using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Cockpit.App.Controls;

// The focus pane and the miniature rail, split by one draggable divider (AC-443): an extension of
// SessionTilePanel's gutter-drag, reusing `StackPaneMath` rather than a second resize mechanism. Expects
// exactly two children — index 0 is the focus content, index 1 the rail content (typically a
// ScrollViewer wrapping an ItemsControl whose ItemsPanel is `RailTilePanel`).
public sealed class FocusRailPanel : Panel
{
    private const double Gutter = 8;
    private const double GrabTolerance = 4;

    // The rail can't be dragged narrower than one tile; the focus pane can't be dragged unusably thin.
    // Both sides of AC-443's acceptance criterion #1.
    public const double MinRailWidth = 160;
    public const double MinFocusWidth = 240;

    public static readonly StyledProperty<double> RailWeightProperty =
        AvaloniaProperty.Register<FocusRailPanel, double>(nameof(RailWeight), 0.3);

    // Non-null while the divider is being dragged: the weights StackPaneMath.Resize started from, and the
    // pointer's X at press time.
    private (double[] StartWeights, double StartX)? _drag;

    static FocusRailPanel()
    {
        AffectsMeasure<FocusRailPanel>(RailWeightProperty);
        AffectsArrange<FocusRailPanel>(RailWeightProperty);
    }

    public FocusRailPanel()
    {
        Background = Brushes.Transparent;
    }

    // Weight of the rail against the focus pane's fixed 1.0 — StackPaneMath weights are proportional, only
    // the ratio matters. Persisted as-is per workspace (`CockpitViewModel.FocusRailWeight`).
    public double RailWeight
    {
        get => GetValue(RailWeightProperty);
        set => SetValue(RailWeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2)
        {
            foreach (var child in Children)
            {
                child.Measure(availableSize);
            }

            return availableSize;
        }

        var slots = Slots(availableSize.Width);
        Children[0].Measure(new Size(slots[0].Height, availableSize.Height));
        Children[1].Measure(new Size(slots[1].Height, availableSize.Height));
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2)
        {
            foreach (var child in Children)
            {
                child.Arrange(new Rect(finalSize));
            }

            return finalSize;
        }

        var slots = Slots(finalSize.Width);
        Children[0].Arrange(new Rect(slots[0].Top, 0, slots[0].Height, finalSize.Height));
        Children[1].Arrange(new Rect(slots[1].Top, 0, slots[1].Height, finalSize.Height));
        return finalSize;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_drag is { } drag)
        {
            var updated = StackPaneMath.Resize(drag.StartWeights, 0, p.X - drag.StartX, Bounds.Width - Gutter, MinFocusWidth, MinRailWidth);
            RailWeight = updated[0] > 0 ? updated[1] / updated[0] : RailWeight;
            e.Handled = true;
            return;
        }

        Cursor = GutterAt(p.X) == 0 ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || !ReferenceEquals(e.Source, this) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var p = e.GetPosition(this);
        if (GutterAt(p.X) != 0)
        {
            return;
        }

        _drag = ([1.0, RailWeight], p.X);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not null)
        {
            _drag = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private int GutterAt(double x) => StackPaneMath.GutterAt(Slots(Bounds.Width), x, Gutter, GrabTolerance);

    private IReadOnlyList<StackPaneMath.Slot> Slots(double width) =>
        StackPaneMath.Layout([1.0, RailWeight], width, Gutter);
}
