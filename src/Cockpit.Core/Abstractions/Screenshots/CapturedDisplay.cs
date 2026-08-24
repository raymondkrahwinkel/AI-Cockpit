namespace Cockpit.Core.Abstractions.Screenshots;

// AC-1013: One display in a `ScreenCapture` — desktop position, scale, and composed-image pixels (AC-333).
// The layout must travel with the image because scaling is per display, not per desktop, so a display's image
// offset cannot be derived from its desktop position alone (Spectacle has an open bug in this seam, KDE#502047).
public sealed record CapturedDisplay
{
    // Where this display sits on the virtual desktop, in whatever coordinates that desktop uses — device pixels
    // on Windows, the compositor's logical layout under Wayland. Not the same space as `ImageBounds`
    // unless `Scale` is 1, and never assumed to be.
    public required CaptureRect DesktopBounds { get; init; }

    // AC-1013: The desktop's reported scale factor, carried for callers sizing something in display pixels
    // (blur radius, handle). The mapping below ignores it — DesktopBounds/ImageBounds already encode the
    // actual ratio, rounding included, and one source beats two that can disagree.
    public required double Scale { get; init; }

    // Where this display's pixels sit in the composed image, in that image's own pixels.
    public required CaptureRect ImageBounds { get; init; }

    // The first pixel of the composed image belonging to a point on the desktop — the top-left corner a crop
    // starting there begins at. The caller has established the point is on this display;
    // `ScreenCapture.ToImagePixel` is the entry point that checks.
    public CapturePoint ToImagePixel(CapturePoint desktopPoint) =>
        new(
            ImageBounds.X + _FirstPixelOf(desktopPoint.X - DesktopBounds.X, ImageBounds.Width, DesktopBounds.Width),
            ImageBounds.Y + _FirstPixelOf(desktopPoint.Y - DesktopBounds.Y, ImageBounds.Height, DesktopBounds.Height));

    // AC-1013: The desktop point a pixel of the composed image belongs to, for comparing against the operator's
    // pointer. A desktop point round-trips exactly (every real display is scaled >=100%); a pixel does not — at
    // scale-up several pixels answer to one desktop position, so pixel->desktop->pixel lands on that position's first pixel, not a defect.
    public CapturePoint ToDesktopPoint(CapturePoint imagePixel) =>
        new(
            DesktopBounds.X + _PositionOf(imagePixel.X - ImageBounds.X, DesktopBounds.Width, ImageBounds.Width),
            DesktopBounds.Y + _PositionOf(imagePixel.Y - ImageBounds.Y, DesktopBounds.Height, ImageBounds.Height));

    // AC-1013: One convention read from either end — desktop position n covers image pixels [n*s, (n+1)*s), so
    // the first pixel rounds up and the owning position rounds down; rounding both to nearest looks symmetric
    // but is wrong (at 125% it would put desktop 3 on pixel 3, which belongs to desktop 2).
    private static int _FirstPixelOf(int position, int imageExtent, int desktopExtent) =>
        (int)Math.Ceiling(position * (double)imageExtent / desktopExtent);

    private static int _PositionOf(int pixel, int desktopExtent, int imageExtent) =>
        (int)Math.Floor(pixel * (double)desktopExtent / imageExtent);
}
