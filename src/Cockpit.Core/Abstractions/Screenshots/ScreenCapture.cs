namespace Cockpit.Core.Abstractions.Screenshots;

// AC-333: what a capture hands back — pixels plus the desktop they came off. Raw bytes alone suffice to attach
// to a session but not to select from, since nothing in a PNG maps a pointer position to its pixel.
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
