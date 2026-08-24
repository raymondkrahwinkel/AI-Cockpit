namespace Cockpit.Core.Abstractions.Screenshots;

// One length of a curve the way it is drawn — two control points and where it ends, the start being wherever the
// piece before it finished (AC-362).
public readonly record struct StrokeCurve(MarkPoint FirstControl, MarkPoint SecondControl, MarkPoint End);

// AC-362: freehand line, `Points`/`Colour`/`Thickness`. First mark whose fidelity depends on pointer sampling
// rate — deleted: fast drags leave samples far apart, so straight-line joins draw a polygon; `Curve` fixes this.
public sealed record StrokeMark(IReadOnlyList<CapturePoint> Points, uint Colour, int Thickness) : Mark
{
    // How far the pointer must travel before another point is worth keeping, in the image's pixels. Points closer
    // together than this say nothing about the gesture and a great deal about how the hand shook and how often
    // the window was asked where the pointer was.
    private const double ShortestStep = 3;

    // How far each length of curve reaches towards its neighbours. A sixth is what turns a chain of points into
    // the Catmull-Rom curve through them: enough that the line bends where the hand bent, not so much that it
    // overshoots into a loop the hand never made.
    private const double Reach = 6;

    // The points worth keeping: the ones far enough apart to be about the gesture rather than about the hand and
    // the sampling. The first and the last are always kept — a stroke starts and ends where the operator says.
    public IReadOnlyList<CapturePoint> Thinned()
    {
        if (Points.Count == 0)
        {
            return [];
        }

        var kept = new List<CapturePoint> { Points[0] };
        foreach (var point in Points.Skip(1))
        {
            if (_Distance(kept[^1], point) >= ShortestStep)
            {
                kept.Add(point);
            }
        }

        if (kept.Count > 1 && kept[^1] != Points[^1])
        {
            kept[^1] = Points[^1];
        }

        return kept;
    }

    // AC-1013: curves through kept points, shared by preview and delivered picture. Deleted: worked example —
    // straight-line joins at a fast drag turn a circle into a visible hexagon.
    public IReadOnlyList<StrokeCurve> Curve()
    {
        if (Thinned() is not { Count: > 1 } points)
        {
            return [];
        }

        var curves = new List<StrokeCurve>(points.Count - 1);
        for (var index = 0; index < points.Count - 1; index++)
        {
            // The two neighbours either side of this length decide which way it leans. At the ends there is no
            // neighbour, so the end point stands in for it and the curve leaves straight, which is what a line
            // that starts under your hand does.
            var before = points[Math.Max(index - 1, 0)];
            var start = points[index];
            var end = points[index + 1];
            var after = points[Math.Min(index + 2, points.Count - 1)];

            curves.Add(new StrokeCurve(
                new MarkPoint(start.X + ((end.X - before.X) / Reach), start.Y + ((end.Y - before.Y) / Reach)),
                new MarkPoint(end.X - ((after.X - start.X) / Reach), end.Y - ((after.Y - start.Y) / Reach)),
                new MarkPoint(end.X, end.Y)));
        }

        return curves;
    }

    // Where the line begins, for a drawer that needs somewhere to put the pen down before the first curve.
    public MarkPoint? Start() =>
        Thinned() is { Count: > 1 } points ? new MarkPoint(points[0].X, points[0].Y) : null;

    // Moved into the crop's space and left whole, the way the arrow and the frame are. A stroke trimmed at the
    // edge would end where the operator did not lift their hand, and the ends of a gesture are most of what it
    // says — a circle cut open is not a circle.
    public override Mark? ClipTo(CaptureRect region) =>
        Bounds() is { } bounds && bounds.Overlap(region) is not null
            ? this with
            {
                Points = Points.Select(point => new CapturePoint(point.X - region.X, point.Y - region.Y)).ToList(),
            }
            : null;

    // The whole-pixel box the stroke paints inside, ring included, or nothing where there is no stroke.
    public CaptureRect? Bounds()
    {
        if (Thinned() is not { Count: > 1 } points)
        {
            return null;
        }

        // Half the line, and a pixel for what antialiasing puts past that.
        var margin = (Thickness / 2.0) + 1;
        var left = (int)Math.Floor(points.Min(point => point.X) - margin);
        var top = (int)Math.Floor(points.Min(point => point.Y) - margin);
        var right = (int)Math.Ceiling(points.Max(point => point.X) + margin);
        var bottom = (int)Math.Ceiling(points.Max(point => point.Y) + margin);

        return new CaptureRect(left, top, right - left, bottom - top);
    }

    private static double _Distance(CapturePoint from, CapturePoint to) =>
        Math.Sqrt(Math.Pow((double)to.X - from.X, 2) + Math.Pow((double)to.Y - from.Y, 2));
}
