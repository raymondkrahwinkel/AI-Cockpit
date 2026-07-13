using Avalonia;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Model;
using Path = Avalonia.Controls.Shapes.Path;

namespace Cockpit.Plugin.Workflows.Canvas;

/// <summary>
/// A wire between two pins, drawn as the bezier every flow tool draws: the curve leaves a node sideways rather
/// than diving at it. The only real geometry in the editor.
/// </summary>
internal sealed class WorkflowWire(WorkflowConnection connection, WorkflowPin from, WorkflowPin to)
{
    private const double MinTangent = 40;
    private const double MaxTangent = 160;

    public Path Line { get; } = NewLine();

    public bool Touches(string nodeId) =>
        connection.FromNodeId == nodeId || connection.ToNodeId == nodeId;

    public void Redraw(Visual surface) => Draw(Line, from.AnchorOn(surface), to.AnchorOn(surface));

    public static Path NewLine() => new()
    {
        Stroke = new SolidColorBrush(Color.Parse("#7A7A88")),
        StrokeThickness = 2,
        // A wire is not something you click — clicking through it hits the canvas underneath, which is what an
        // operator expects when they drag the background to pan.
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
}
