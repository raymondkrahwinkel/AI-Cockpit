namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// What a capture hands back (AC-333): the pixels, and the desktop they came off. Raw image bytes on their own
/// are enough to attach to a session but not to select from — a selection UI has a pointer position on the
/// desktop and needs the pixel underneath it, which nothing in a PNG can answer.
/// </summary>
/// <remarks>
/// The image spans every display at once, so the displays are what make sense of it. Where one contributed which
/// part of it, and by how much the desktop scaled it, is <see cref="CapturedDisplay"/>'s.
/// </remarks>
public sealed record ScreenCapture
{
    /// <summary>The whole desktop as PNG bytes — every display, composed into one image.</summary>
    public required byte[] Image { get; init; }

    /// <summary>
    /// The displays that make up <see cref="Image"/>, in no particular order. Every platform capture reports
    /// them since AC-326/327/328; a capture that could not say where its pixels came from refuses rather than
    /// handing back an image nothing can be selected from.
    /// </summary>
    public required IReadOnlyList<CapturedDisplay> Displays { get; init; }

    /// <summary>The display a point on the desktop falls on, or <see langword="null"/> when it falls on none of them.</summary>
    public CapturedDisplay? DisplayAt(CapturePoint desktopPoint) =>
        Displays.FirstOrDefault(display => display.DesktopBounds.Contains(desktopPoint));

    /// <summary>
    /// The pixel of <see cref="Image"/> under a point on the desktop, or <see langword="null"/> when no display
    /// covers that point — a pointer in the gap an L-shaped arrangement leaves, or a capture with no layout.
    /// </summary>
    public CapturePoint? ToImagePixel(CapturePoint desktopPoint) =>
        DisplayAt(desktopPoint)?.ToImagePixel(desktopPoint);

    /// <summary>
    /// The point on the desktop a pixel of <see cref="Image"/> came from, or <see langword="null"/> when no
    /// display owns that pixel.
    /// </summary>
    public CapturePoint? ToDesktopPoint(CapturePoint imagePixel) =>
        Displays.FirstOrDefault(display => display.ImageBounds.Contains(imagePixel))?.ToDesktopPoint(imagePixel);
}
