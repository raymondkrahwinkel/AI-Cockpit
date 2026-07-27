namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// A whole-number rectangle in one of a capture's two coordinate spaces — a display's place on the desktop, or
/// its pixels in the composed image. Same caveat as <see cref="CapturePoint"/>: the space is the property's to
/// name, not the type's.
/// </summary>
public readonly record struct CaptureRect(int X, int Y, int Width, int Height)
{
    /// <summary>One past the last column, the way <c>X + Width</c> reads everywhere else — exclusive.</summary>
    public int Right => X + Width;

    /// <summary>One past the last row — exclusive, like <see cref="Right"/>.</summary>
    public int Bottom => Y + Height;

    /// <summary>
    /// Whether the point falls inside. Half-open on both axes, so two displays laid edge to edge claim a point on
    /// the seam exactly once — the alternative is a pointer on the boundary belonging to both screens and the
    /// crop landing on whichever was enumerated first.
    /// </summary>
    public bool Contains(CapturePoint point) =>
        point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;

    /// <summary>
    /// The part of this rectangle that also lies in the other one, or nothing where they do not meet. Half-open
    /// like <see cref="Contains"/>, so two rectangles that merely touch along an edge share no area.
    /// </summary>
    public CaptureRect? Overlap(CaptureRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);

        return right > left && bottom > top ? new CaptureRect(left, top, right - left, bottom - top) : null;
    }
}
