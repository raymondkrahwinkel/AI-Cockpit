namespace Cockpit.Core.Abstractions.Screenshots;

// An arrow pointing at one thing on a busy screen (AC-360) — the mark this whole epic is really about, since
// every other tool says *this area* and only this one says *that*.
//
// `From`: Where the drag began, in the pixels of whichever image it is being spoken about in. The tail, so an arrow can come in from an empty part of the picture rather than lying over the thing it indicates.
// `To`: Where it points. The head sits here and turns to face this way.
// `Colour`: The body's colour as 0xAARRGGBB, carried for the same reason `OutlineMark` carries it.
// `Thickness`: The thinnest the shaft is ever drawn, in the image's pixels — a floor rather than the width itself. See `Weight`.
public sealed record ArrowMark(CapturePoint From, CapturePoint To, uint Colour, int Thickness) : Mark
{
    // How heavy the arrow is drawn for its length, before the limits below. The whole arrow scales, never the
    // head on its own: scaling one part of a shape and leaving the rest is what turns a long arrow into a
    // triangle on the end of a thread — which is exactly what the first attempt at this looked like on screen.
    private const double WeightOfLength = 0.03;

    // The heaviest it may be drawn, in multiples of the thinnest. Without a ceiling an arrow drawn across a large
    // capture keeps thickening until it is a bar lying over the thing it is meant to be pointing at.
    private const double HeaviestInThicknesses = 4;

    // How far the head reaches back from the tip, in weights. With the width below it puts the tip a little under a right angle, which is what reads as an arrow rather than as a dart or a wedge.
    private const double HeadInWeights = 3.5;

    // How wide the head is across the barbs, in weights. Three shafts wide is the proportion a drawn arrow has had since long before there were screens to draw one on.
    private const double HeadWidthInWeights = 3;

    // How much of the whole arrow the head may take. The tie-breaker for a very short drag, where the head worked
    // out from the weight would come out longer than the arrow it belongs to — past this the shape stops being an
    // arrow and becomes a triangle with a stub behind it.
    private const double MostOfTheArrow = 0.6;

    // How thick this arrow is actually drawn, in the image's pixels: proportional to its own length, never below
    // the thickness it was given and never more than a few times it.
    // This is the answer to how the head scales — it does not scale on its own. Every measurement of the shape is
    // a multiple of this one number, so a short arrow and a long one are one drawing at two sizes rather than two
    // marks that happen to share a name, and an operator who puts both on the same screenshot sees one tool.
    public double Weight => Math.Clamp(_Length * WeightOfLength, Thickness, Thickness * HeaviestInThicknesses);

    // The outline of the whole arrow — shaft and head as one closed shape, running from one side of the tail
    // round the tip and back. Empty for a drag that went nowhere, which is not an arrow: it has no direction to
    // point in.
    // Worked out here rather than by each drawer so that the preview on the surface and the shape burnt into the
    // picture cannot be two different arrows. They are drawn by different libraries; this is the one place that
    // decides what is being drawn.
    public IReadOnlyList<MarkPoint> Silhouette()
    {
        var length = _Length;
        if (length <= 0)
        {
            return [];
        }

        // The direction, and the same turned a quarter turn — every corner below is the tip or the tail plus so
        // much along one and so much across the other, which is what makes the shape follow the drag instead of
        // being drawn at whatever angle it was written down at.
        var alongUnitX = (To.X - From.X) / length;
        var alongUnitY = (To.Y - From.Y) / length;
        var acrossUnitX = -alongUnitY;
        var acrossUnitY = alongUnitX;

        var weight = Weight;
        var head = Math.Min(weight * HeadInWeights, length * MostOfTheArrow);
        var barb = weight * HeadWidthInWeights / 2;
        var shaft = weight / 2;

        var neckX = To.X - (alongUnitX * head);
        var neckY = To.Y - (alongUnitY * head);

        return
        [
            _Offset(From.X, From.Y, acrossUnitX, acrossUnitY, shaft),
            _Offset(neckX, neckY, acrossUnitX, acrossUnitY, shaft),
            _Offset(neckX, neckY, acrossUnitX, acrossUnitY, barb),
            new MarkPoint(To.X, To.Y),
            _Offset(neckX, neckY, acrossUnitX, acrossUnitY, -barb),
            _Offset(neckX, neckY, acrossUnitX, acrossUnitY, -shaft),
            _Offset(From.X, From.Y, acrossUnitX, acrossUnitY, -shaft),
        ];
    }

    // Moved into the crop's space and left whole, the way a frame is and for the same reason: an arrow trimmed at
    // the tip would end in a flat cut where the operator drew a point, and one trimmed at the tail would start
    // somewhere they did not begin. What falls outside is simply not painted.
    // Dropped only when the shape cannot reach the region at all. The test is the shape's own bounding box, which
    // can say yes to a diagonal whose ink misses the region entirely — that costs a mark that paints nothing, and
    // is the cheap side to be wrong on. Being wrong the other way would rub out an arrow that was visible.
    public override Mark? ClipTo(CaptureRect region) =>
        Bounds() is { } bounds && bounds.Overlap(region) is not null
            ? this with
            {
                From = new CapturePoint(From.X - region.X, From.Y - region.Y),
                To = new CapturePoint(To.X - region.X, To.Y - region.Y),
            }
            : null;

    // The whole-pixel box the arrow paints inside, ring included, or nothing where there is no arrow. Rounded
    // outwards on every side: a box rounded to the nearest pixel cuts the half-pixel of ink that lands on the
    // pixel past it.
    public CaptureRect? Bounds()
    {
        if (Silhouette() is not { Count: > 0 } corners)
        {
            return null;
        }

        // A pixel for what antialiasing puts past the shape's own corners.
        const double margin = 1;
        var left = (int)Math.Floor(corners.Min(corner => corner.X) - margin);
        var top = (int)Math.Floor(corners.Min(corner => corner.Y) - margin);
        var right = (int)Math.Ceiling(corners.Max(corner => corner.X) + margin);
        var bottom = (int)Math.Ceiling(corners.Max(corner => corner.Y) + margin);

        return new CaptureRect(left, top, right - left, bottom - top);
    }

    private double _Length => Math.Sqrt(
        Math.Pow((double)To.X - From.X, 2) + Math.Pow((double)To.Y - From.Y, 2));

    private static MarkPoint _Offset(double x, double y, double acrossX, double acrossY, double distance) =>
        new(x + (acrossX * distance), y + (acrossY * distance));
}
