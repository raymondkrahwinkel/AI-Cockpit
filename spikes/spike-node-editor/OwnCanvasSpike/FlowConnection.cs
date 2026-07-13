using Avalonia;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace OwnCanvasSpike;

/// <summary>
/// A wire between two pins, drawn as the bezier every flow tool draws: horizontal tangents out of the pins, so
/// the curve leaves the node sideways rather than diving at it. This is the one piece of real geometry in the
/// whole editor — and it is a dozen lines.
/// </summary>
internal sealed class FlowConnection(FlowPin from, FlowPin to)
{
    public Path Line { get; } = NewLine();

    public bool Touches(FlowNode node) => ReferenceEquals(from.Owner, node) || ReferenceEquals(to.Owner, node);

    public void Redraw()
    {
        var surface = Line.Parent as Visual;
        if (surface is null)
        {
            return;
        }

        Draw(Line, from.AnchorOn(surface), to.AnchorOn(surface));
    }

    public static Path NewLine() => new()
    {
        Stroke = new SolidColorBrush(Color.Parse("#7A7A88")),
        StrokeThickness = 2,
        IsHitTestVisible = false,
    };

    public static void Draw(Path line, Point start, Point end)
    {
        // The further apart the pins, the lazier the curve — a fixed tangent looks cramped up close and limp
        // across the canvas.
        var tangent = Math.Clamp(Math.Abs(end.X - start.X) * 0.5, 40, 160);

        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments!.Add(new BezierSegment
        {
            Point1 = start.WithX(start.X + tangent),
            Point2 = end.WithX(end.X - tangent),
            Point3 = end,
        });

        geometry.Figures!.Add(figure);
        line.Data = geometry;
    }
}
