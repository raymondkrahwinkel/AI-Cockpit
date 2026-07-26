namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// One display in a <see cref="ScreenCapture"/>: where it sits on the desktop, how the desktop scales it, and
/// where its pixels ended up in the composed image (AC-333).
/// </summary>
/// <remarks>
/// The layout has to travel with the image because scaling is per display, not per desktop. A 150% laptop panel
/// beside a 100% monitor means the two halves of the image relate to the desktop's own coordinates by different
/// factors, and a crop has to happen in the image's pixels — so the offset of a display's pixels cannot be
/// derived from where it sits on the desktop. In that example the second display starts at desktop x = 1920 but
/// at image x = 2880, because the first contributed 1920 × 1.5 columns. Spectacle has an open bug in exactly
/// this seam (KDE#502047).
/// </remarks>
public sealed record CapturedDisplay
{
    /// <summary>
    /// Where this display sits on the virtual desktop, in whatever coordinates that desktop uses — device pixels
    /// on Windows, the compositor's logical layout under Wayland. Not the same space as <see cref="ImageBounds"/>
    /// unless <see cref="Scale"/> is 1, and never assumed to be.
    /// </summary>
    public required CaptureRect DesktopBounds { get; init; }

    /// <summary>
    /// What the desktop reports as this display's scale factor — 1.0, 1.5, 2.0. Carried for callers that have to
    /// size something in a display's own pixels (a blur radius, a handle) and would otherwise have no way to ask.
    /// The mapping below does not use it: <see cref="DesktopBounds"/> and <see cref="ImageBounds"/> already say
    /// what the ratio actually came out as, rounding included, and one source beats two that can disagree.
    /// </summary>
    public required double Scale { get; init; }

    /// <summary>Where this display's pixels sit in the composed image, in that image's own pixels.</summary>
    public required CaptureRect ImageBounds { get; init; }

    /// <summary>
    /// The first pixel of the composed image belonging to a point on the desktop — the top-left corner a crop
    /// starting there begins at. The caller has established the point is on this display;
    /// <see cref="ScreenCapture.ToImagePixel"/> is the entry point that checks.
    /// </summary>
    public CapturePoint ToImagePixel(CapturePoint desktopPoint) =>
        new(
            ImageBounds.X + _FirstPixelOf(desktopPoint.X - DesktopBounds.X, ImageBounds.Width, DesktopBounds.Width),
            ImageBounds.Y + _FirstPixelOf(desktopPoint.Y - DesktopBounds.Y, ImageBounds.Height, DesktopBounds.Height));

    /// <summary>
    /// The desktop point a pixel of the composed image belongs to — the way back from a crop to something the
    /// operator's pointer can be compared against.
    /// </summary>
    /// <remarks>
    /// A desktop point round-trips through this exactly for any display the image is at least as wide and tall as
    /// — which is every real one, since a desktop never scales a display below 100%. A pixel does not round-trip:
    /// where the display is scaled up, several pixels answer to one desktop position, so pixel → desktop → pixel
    /// lands on the first pixel of that position rather than the one it started from. That is the scaling, not a
    /// defect here. Were a display ever scaled <em>down</em>, the loss would run the other way and desktop points
    /// would stop round-tripping too.
    /// </remarks>
    public CapturePoint ToDesktopPoint(CapturePoint imagePixel) =>
        new(
            DesktopBounds.X + _PositionOf(imagePixel.X - ImageBounds.X, DesktopBounds.Width, ImageBounds.Width),
            DesktopBounds.Y + _PositionOf(imagePixel.Y - ImageBounds.Y, DesktopBounds.Height, ImageBounds.Height));

    // The two are one convention read from either end: desktop position n covers image pixels [n·s, (n+1)·s), so
    // the first pixel of a position rounds up and the position owning a pixel rounds down. Rounding both to
    // nearest would look symmetric and be wrong — at 125% it puts desktop 3 on pixel 3, which belongs to
    // desktop 2.
    private static int _FirstPixelOf(int position, int imageExtent, int desktopExtent) =>
        (int)Math.Ceiling(position * (double)imageExtent / desktopExtent);

    private static int _PositionOf(int pixel, int desktopExtent, int imageExtent) =>
        (int)Math.Floor(pixel * (double)desktopExtent / imageExtent);
}
