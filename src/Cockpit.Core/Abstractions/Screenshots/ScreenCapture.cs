namespace Cockpit.Core.Abstractions.Screenshots;

// What a capture hands back (AC-333): the pixels, and the desktop they came off. Raw image bytes on their own
// are enough to attach to a session but not to select from — a selection UI has a pointer position on the
// desktop and needs the pixel underneath it, which nothing in a PNG can answer.
// The image spans every display at once, so the displays are what make sense of it. Where one contributed which
// part of it, and by how much the desktop scaled it, is `CapturedDisplay`'s.
public sealed record ScreenCapture
{
    // The whole desktop as PNG bytes — every display, composed into one image.
    public required byte[] Image { get; init; }

    // The displays that make up `Image`, in no particular order. Every platform capture reports
    // them since AC-326/327/328; a capture that could not say where its pixels came from refuses rather than
    // handing back an image nothing can be selected from.
    public required IReadOnlyList<CapturedDisplay> Displays { get; init; }

    // The display a point on the desktop falls on, or `null` when it falls on none of them.
    public CapturedDisplay? DisplayAt(CapturePoint desktopPoint) =>
        Displays.FirstOrDefault(display => display.DesktopBounds.Contains(desktopPoint));

    // The pixel of `Image` under a point on the desktop, or `null` when no display
    // covers that point — a pointer in the gap an L-shaped arrangement leaves, or a capture with no layout.
    public CapturePoint? ToImagePixel(CapturePoint desktopPoint) =>
        DisplayAt(desktopPoint)?.ToImagePixel(desktopPoint);

    // The point on the desktop a pixel of `Image` came from, or `null` when no
    // display owns that pixel.
    public CapturePoint? ToDesktopPoint(CapturePoint imagePixel) =>
        Displays.FirstOrDefault(display => display.ImageBounds.Contains(imagePixel))?.ToDesktopPoint(imagePixel);
}
