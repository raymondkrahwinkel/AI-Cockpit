namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// Something the operator put on the capture (AC-359) — a box to obscure, a frame to point at. Marks are held as
/// an ordered list and burnt into the pixels when the shot is confirmed, never carried alongside the image.
/// </summary>
/// <remarks>
/// The burning-in is the whole design. What reaches the model is one array of bytes; a mark that lives beside it
/// is a mark the model cannot see, and — for a redaction — one that a path forgetting to composite would quietly
/// drop. That rule is AC-331's and this generalises it rather than loosening it.
/// <para>
/// Geometry belongs to each kind rather than to this type: a frame is a rectangle, an arrow will be two points,
/// a stroke a run of them. What they share is being placed in the capture's own pixels and having to survive the
/// crop, which is what <see cref="ClipTo"/> is for.
/// </para>
/// </remarks>
public abstract record Mark
{
    /// <summary>
    /// The mark as it sits in a cropped image, or nothing where it falls entirely outside the region and is not
    /// in the picture at all. Clipped rather than dropped whenever any part of it survives: an operator who drew
    /// a box half over the edge of what they are sending still meant the half that is in it.
    /// </summary>
    /// <param name="region">The region being taken, in the capture's pixels.</param>
    public abstract Mark? ClipTo(CaptureRect region);
}
