using Avalonia;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;
using Path = Avalonia.Controls.Shapes.Path;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// A wire between two steps (#69): a bezier that leaves sideways, an arrowhead where it arrives, and — on a
/// decision — the name of the branch on the line itself. The arrowhead is not decoration: with fan-out and loops
/// allowed, "which way does this one run" stops being obvious from the shape alone.
/// </summary>
internal sealed class WorkflowWire
{
    private const double MinTangent = 40;
    private const double MaxTangent = 140;
    private const double ArrowSize = 7;

    private readonly WorkflowConnection _connection;
    private readonly WorkflowPin _from;
    private readonly WorkflowPin _to;

    public WorkflowWire(WorkflowConnection connection, WorkflowPin from, WorkflowPin to, string? branchLabel)
    {
        _connection = connection;
        _from = from;
        _to = to;

        Line = NewLine();
        Arrow = new Path { Fill = WireBrush, IsHitTestVisible = false };

        // A wire two pixels wide is a wire you cannot hit. This one is invisible, wide, and lies on top: it is what
        // the pointer actually meets, and it is the only reason a connection can be removed at all.
        Hit = new Path
        {
            Stroke = Brushes.Transparent,
            StrokeThickness = 14,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        Remove = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#22222A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3C3C46")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Width = 18,
            Height = 18,
            IsVisible = false,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = "✕",
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            },
        };

        ToolTip.SetTip(Remove, "Remove this connection");

        // Shown while the pointer is on the wire or on the button itself — otherwise reaching for the ✕ would take
        // the pointer off the wire and the ✕ would vanish under your hand.
        Hit.PointerEntered += (_, _) => Remove.IsVisible = true;
        Hit.PointerExited += (_, _) => Remove.IsVisible = Remove.IsPointerOver;
        Remove.PointerExited += (_, _) => Remove.IsVisible = Hit.IsPointerOver;
        Remove.PointerPressed += (_, e) =>
        {
            RemoveRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };

        // Only a branch that has a name gets a label: "true"/"false" on a decision means something, an empty
        // label on every other wire is noise.
        Label = string.IsNullOrEmpty(branchLabel)
            ? null
            : new Border
            {
                Background = new SolidColorBrush(Color.Parse("#22222A")),
                BorderBrush = new SolidColorBrush(Color.Parse("#3C3C46")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1),
                IsHitTestVisible = false,
                Child = new TextBlock { Text = branchLabel, FontSize = 9, Opacity = 0.8 },
            };
    }

    /// <summary>Raised when the operator clicked the wire's ✕.</summary>
    public event EventHandler? RemoveRequested;

    public Path Line { get; }

    /// <summary>The wide, invisible curve the pointer meets.</summary>
    public Path Hit { get; }

    /// <summary>The ✕ on the middle of the wire, shown on hover.</summary>
    public Border Remove { get; }

    public Path Arrow { get; }

    public Border? Label { get; }

    public WorkflowConnection Connection => _connection;

    public bool Touches(string nodeId) =>
        _connection.FromNodeId == nodeId || _connection.ToNodeId == nodeId;

    public void Redraw(Avalonia.Controls.Canvas surface)
    {
        var start = _from.AnchorOn(surface);
        var end = _to.AnchorOn(surface);

        Draw(Line, start, end);
        Draw(Hit, start, end);
        _DrawArrow(end);

        // The middle of a bezier with flat tangents sits at the midpoint of its ends, near enough for a button.
        Avalonia.Controls.Canvas.SetLeft(Remove, (start.X + end.X) / 2 - 9);
        Avalonia.Controls.Canvas.SetTop(Remove, (start.Y + end.Y) / 2 - 9);

        if (Label is not null)
        {
            // Near the source, where the branch splits — that is where the reader asks which way is which.
            Avalonia.Controls.Canvas.SetLeft(Label, start.X + 12);
            Avalonia.Controls.Canvas.SetTop(Label, start.Y - 9);
        }
    }

    public static Path NewLine() => new()
    {
        Stroke = WireBrush,
        StrokeThickness = 2,
        // Clicking a wire hits the canvas underneath, which is what an operator expects when they drag the
        // background to pan.
        IsHitTestVisible = false,
    };

    public static void Draw(Path line, Point start, Point end)
    {
        // The further apart the pins, the lazier the curve: a fixed tangent looks cramped up close and limp
        // across the canvas.
        var tangent = Math.Clamp(Math.Abs(end.X - start.X) * 0.5, MinTangent, MaxTangent);

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments!.Add(new BezierSegment
        {
            Point1 = start.WithX(start.X + tangent),
            Point2 = end.WithX(end.X - tangent),
            Point3 = end,
        });

        var geometry = new PathGeometry();
        geometry.Figures!.Add(figure);
        line.Data = geometry;
    }

    // The curve arrives horizontally (its last tangent is flat), so the arrow always points right — no need to
    // differentiate the bezier for an angle it cannot have.
    private void _DrawArrow(Point end)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = end, IsClosed = true };
        figure.Segments!.Add(new LineSegment { Point = new Point(end.X - ArrowSize, end.Y - ArrowSize / 1.6) });
        figure.Segments!.Add(new LineSegment { Point = new Point(end.X - ArrowSize, end.Y + ArrowSize / 1.6) });
        geometry.Figures!.Add(figure);

        Arrow.Data = geometry;
    }

    // The cockpit's own hairline, so a wire belongs to this app rather than to the one we borrowed the shape from.
    private static IBrush WireBrush { get; } =
        Application.Current?.TryFindResource("CockpitTextFaintBrush", out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse("#6E6E7C"));
}
