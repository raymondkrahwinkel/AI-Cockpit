namespace Cockpit.Core.Abstractions.Screenshots;

// One length of a curve the way it is drawn — two control points and where it ends, the start being wherever the
// piece before it finished (AC-362).
public readonly record struct StrokeCurve(MarkPoint FirstControl, MarkPoint SecondControl, MarkPoint End);

// A line drawn freehand on the capture (AC-362) — circling a thing, crossing something out, following a path no
// rectangle or arrow can describe.
//
// `Points`: Where the pointer went, in the pixels of whichever image it is being spoken about in, in the order it went there.
// `Colour`: What it is drawn in as 0xAARRGGBB, carried for the same reason the other marks carry it.
// `Thickness`: How thick the line is, in the image's pixels.
// The first mark whose size is not fixed by its shape, and the first whose fidelity depends on how often the
// pointer was heard from. A hand moving quickly over a window that is repainting slowly leaves samples far apart,
// and joining those with straight lines draws a polygon where a curve was made — which is why what is kept is
// `Curve` rather than the points themselves.
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

    // The stroke as a run of curves through the points it kept, or nothing where the gesture never left its
    // starting place. Both the surface's preview and the picture that is sent are drawn from this, so the line the
    // operator watches being made is the line they hand over.
    // A curve through the points rather than lines between them. The alternative is visible and cannot be tuned
    // away: at a fast drag the samples are tens of pixels apart, and a circle drawn quickly comes out a hexagon.
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
