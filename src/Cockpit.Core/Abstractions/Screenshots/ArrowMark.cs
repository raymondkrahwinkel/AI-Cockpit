namespace Cockpit.Core.Abstractions.Screenshots;

// An arrow pointing at one thing on a busy screen (AC-360) — every other tool says *this area*, only this
// one says *that*. `From`/`To` are tail/head in image pixels; `Colour` is 0xAARRGGBB; `Thickness` is the
// shaft's thinnest draw width, a floor not the width itself — see `Weight`.
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

    // AC-360: Actual draw thickness in image pixels — proportional to length, floored at Thickness, capped at
    // HeaviestInThicknesses. The head does not scale on its own: every shape measurement is a multiple of this
    // one number, so short and long arrows read as one drawing at two sizes, not two different marks.
    public double Weight => Math.Clamp(_Length * WeightOfLength, Thickness, Thickness * HeaviestInThicknesses);

    // AC-360: The whole arrow's outline (shaft+head, one closed shape); empty for a zero-length drag, which has
    // no direction to point in. Computed here rather than per-drawer so the preview and the burnt-in shape —
    // drawn by different libraries — can't diverge into two different arrows.
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

    // AC-360: Moved into the crop's space and left whole (like a frame) — trimming the tip/tail would end an
    // arrow in a flat cut or a wrong start point; what falls outside is just not painted. Dropped only when the
    // bounding box can't reach the region — a cheap false positive (paints nothing) beats rubbing out a visible arrow.
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
