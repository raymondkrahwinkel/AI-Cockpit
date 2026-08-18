using System.Globalization;
using System.Xml.Linq;

namespace Cockpit.Core.Diagrams;

// A point in the SVG's own units. Not an Avalonia Point: this reads markup, and Core carries no UI framework.
public readonly record struct DiagramPoint(double X, double Y);

// An object's box in SVG units.
public readonly record struct DiagramBounds(double X, double Y, double Width, double Height)
{
    public DiagramPoint Center => new(X + (Width / 2), Y + (Height / 2));

    public bool Contains(DiagramPoint point) =>
        point.X >= X && point.X <= X + Width && point.Y >= Y && point.Y <= Y + Height;
}

// One object on the rendered diagram. `Id` is the node's Mermaid id, or a connection's tail with `To` as its head —
// measured on Mermaider 0.12.2, those data-* values are the source's own ids rather than render ids, so they are the
// same handle the per-object edits (AC-852) take.
public sealed record DiagramObjectAt(string Kind, string Id, string? To, string Label, DiagramBounds Bounds, IReadOnlyList<DiagramPoint> Line)
{
    public const string Node = "node";
    public const string Edge = "edge";

    // How the "jij bewerkt" hold and the agent's refusal name this object (AC-852 uses "from->to" for a connection).
    public string HoldKey => To is null ? Id : $"{Id}->{To}";
}

// The measured way back from a click to a place in the source (AC-841's first acceptance criterion). Mermaider tags
// every node with `data-id` and every connection with `data-from`/`data-to`, and writes no transform= at all, so hit
// testing is point-in-shape in one coordinate system — no matrix chain to rebuild.
public static class DiagramSurfaceMap
{
    // How far off a connection's line a click may land and still count, in SVG units — a 2.25px stroke is too thin to
    // aim at.
    private const double EdgeTolerance = 6;

    // The picture's own width in SVG units, so a caller can tell how many control pixels one unit became. 0 when the
    // markup does not say.
    public static double Width(string svg)
    {
        try
        {
            var root = XDocument.Parse(svg).Root;
            var width = _Number(root!, "width");
            var viewBox = root!.Attribute("viewBox")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return width > 0 || viewBox is not { Length: 4 }
                ? width
                : double.TryParse(viewBox[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var boxed) ? boxed : 0;
        }
        catch (System.Xml.XmlException)
        {
            return 0;
        }
    }

    public static IReadOnlyList<DiagramObjectAt> Read(string svg)
    {
        var objects = new List<DiagramObjectAt>();
        XDocument document;
        try
        {
            document = XDocument.Parse(svg);
        }
        catch (System.Xml.XmlException)
        {
            return objects;
        }

        foreach (var element in document.Descendants())
        {
            var kind = element.Attribute("class")?.Value;
            var label = element.Attribute("data-label")?.Value ?? "";

            // AC-899: an erDiagram writes the same two roles under its own names — `entity` for a box and
            // `er-relationship` (data-entity1/2) for a line — so one map covers both dialects.
            if (kind is "node" or "entity" && element.Attribute("data-id")?.Value is { Length: > 0 } id)
            {
                objects.Add(new DiagramObjectAt(DiagramObjectAt.Node, id, null, label, _Bounds(_Points(element)), []));
            }
            else if (kind is "edge" or "er-relationship"
                     && (element.Attribute("data-from") ?? element.Attribute("data-entity1"))?.Value is { Length: > 0 } from
                     && (element.Attribute("data-to") ?? element.Attribute("data-entity2"))?.Value is { Length: > 0 } to)
            {
                var line = _Points(element);
                objects.Add(new DiagramObjectAt(DiagramObjectAt.Edge, from, to, label, _Bounds(line), line));
            }
        }

        return objects;
    }

    // Nodes first: a connection runs up to the node it points at, so testing lines first would let one steal the
    // click on its own target.
    // ponytail: a node is its bounding box, so a click just past a diamond's slant counts — point-in-polygon if felt.
    public static DiagramObjectAt? At(IReadOnlyList<DiagramObjectAt> objects, DiagramPoint point) =>
        objects.FirstOrDefault(o => o.Kind == DiagramObjectAt.Node && o.Bounds.Contains(point))
        ?? objects.Where(o => o.Kind == DiagramObjectAt.Edge)
            .Select(o => (Object: o, Distance: _DistanceTo(o.Line, point)))
            .Where(hit => hit.Distance <= EdgeTolerance)
            .OrderBy(hit => hit.Distance)
            .Select(hit => hit.Object)
            .FirstOrDefault();

    private static double _DistanceTo(IReadOnlyList<DiagramPoint> line, DiagramPoint point)
    {
        var closest = double.MaxValue;
        for (var i = 0; i + 1 < line.Count; i++)
        {
            closest = Math.Min(closest, _DistanceToSegment(line[i], line[i + 1], point));
        }

        return closest;
    }

    private static double _DistanceToSegment(DiagramPoint a, DiagramPoint b, DiagramPoint point)
    {
        var runX = b.X - a.X;
        var runY = b.Y - a.Y;
        var length = (runX * runX) + (runY * runY);
        var along = length <= 0 ? 0 : Math.Clamp((((point.X - a.X) * runX) + ((point.Y - a.Y) * runY)) / length, 0, 1);
        var offX = a.X + (runX * along) - point.X;
        var offY = a.Y + (runY * along) - point.Y;
        return Math.Sqrt((offX * offX) + (offY * offY));
    }

    private static DiagramBounds _Bounds(IReadOnlyList<DiagramPoint> points)
    {
        if (points.Count == 0)
        {
            return default;
        }

        var left = points.Min(p => p.X);
        var top = points.Min(p => p.Y);
        return new DiagramBounds(left, top, points.Max(p => p.X) - left, points.Max(p => p.Y) - top);
    }

    // The corners of whatever shapes the node is drawn with (rect, circle, ellipse, polygon, line) or, for a
    // connection, the points of its path — Mermaider writes only M/L/Q there, all absolute, so the numbers are the path.
    private static List<DiagramPoint> _Points(XElement element)
    {
        var points = new List<DiagramPoint>();
        foreach (var shape in element.DescendantsAndSelf())
        {
            switch (shape.Name.LocalName)
            {
                case "rect":
                    var x = _Number(shape, "x");
                    var y = _Number(shape, "y");
                    points.Add(new DiagramPoint(x, y));
                    points.Add(new DiagramPoint(x + _Number(shape, "width"), y + _Number(shape, "height")));
                    break;
                case "circle":
                case "ellipse":
                    var cx = _Number(shape, "cx");
                    var cy = _Number(shape, "cy");
                    var rx = shape.Attribute("r") is not null ? _Number(shape, "r") : _Number(shape, "rx");
                    var ry = shape.Attribute("r") is not null ? _Number(shape, "r") : _Number(shape, "ry");
                    points.Add(new DiagramPoint(cx - rx, cy - ry));
                    points.Add(new DiagramPoint(cx + rx, cy + ry));
                    break;
                case "line":
                    points.Add(new DiagramPoint(_Number(shape, "x1"), _Number(shape, "y1")));
                    points.Add(new DiagramPoint(_Number(shape, "x2"), _Number(shape, "y2")));
                    break;
                case "polygon":
                    points.AddRange(_Pairs(shape.Attribute("points")?.Value ?? ""));
                    break;
                case "path":
                    points.AddRange(_Pairs(shape.Attribute("d")?.Value ?? ""));
                    break;
            }
        }

        return points;
    }

    private static double _Number(XElement element, string name) =>
        double.TryParse(element.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    // Every number in the text, read two at a time. Command letters (M/L/Q) carry no coordinates of their own, so
    // dropping them loses nothing — a curve's control point simply counts as one more point on the run.
    private static IEnumerable<DiagramPoint> _Pairs(string text)
    {
        var numbers = new List<double>();
        foreach (var token in text.Split([' ', ',', '\n', '\r', '\t', 'M', 'L', 'Q'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                numbers.Add(value);
            }
        }

        for (var i = 0; i + 1 < numbers.Count; i += 2)
        {
            yield return new DiagramPoint(numbers[i], numbers[i + 1]);
        }
    }
}
