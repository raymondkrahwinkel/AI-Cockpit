using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The selection surface's arithmetic (AC-329): a drag on a window turned into a rectangle of the captured
/// image's own pixels, and back again for drawing. Everything the operator does to a selection lives here rather
/// than in the window, because none of it is visual — and the window is the one part of this nobody can test.
/// </summary>
/// <remarks>
/// One window covers the whole virtual desktop and shows the capture as its background, which is how Flameshot
/// and Spectacle both work: what feels like dragging on the live screen is dragging on a frozen image. That makes
/// the conversion window-size versus image-size, one ratio, and keeps <c>Screens.Scaling</c> out of it — which
/// matters, because on this app's XWayland path those numbers come through XRandR and KDE bug 502390 has them
/// doubled on some fractionally-scaled multi-monitor setups.
/// <para>
/// The ratio is deliberately not assumed to be 1. The window is laid out in logical units and the image is in
/// pixels; on a scaled display those differ, and a selection that ignored it would crop somewhere else entirely.
/// </para>
/// </remarks>
public sealed partial class ScreenshotSelectionViewModel : ObservableObject
{
    private readonly ScreenCapture _capture;
    private readonly IReadOnlyList<(DesktopWindow Window, CaptureRect ImageBounds)>? _windows;
    private CapturePoint? _anchor;

    public ScreenshotSelectionViewModel(
        ScreenCapture capture,
        int imageWidth,
        int imageHeight,
        CaptureRect? lastRegion = null,
        IDesktopWindows? windows = null)
    {
        _capture = capture;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;

        // Enumerated once, here, rather than per pointer move: the capture is already frozen, so a window that
        // moves afterwards has moved on a desktop this picture no longer shows. Reading it again would highlight
        // a rectangle that is not where its pixels are.
        _windows = windows is { IsSupported: true } ? _InImageSpace(windows.Enumerate()) : null;

        // Restored rather than started empty: the same panel gets grabbed over and over, and re-dragging it every
        // time is the difference between a tool and a chore. A region from a desktop that has since changed shape
        // would crop somewhere arbitrary, so it only survives if it still fits.
        if (lastRegion is { } region && _Fits(region))
        {
            Selection = region;
        }
    }

    /// <summary>The captured image's width in its own pixels — not the window's.</summary>
    public int ImageWidth { get; }

    /// <summary>The captured image's height in its own pixels.</summary>
    public int ImageHeight { get; }

    /// <summary>The region the operator has marked out, in image pixels, or nothing yet.</summary>
    [ObservableProperty]
    private CaptureRect? _selection;

    /// <summary>How wide the window is drawing the image, in whatever units it lays out in. Set by the view once it knows.</summary>
    [ObservableProperty]
    private double _surfaceWidth;

    /// <summary>How tall the window is drawing the image.</summary>
    [ObservableProperty]
    private double _surfaceHeight;

    /// <summary>What the operator settled on, once they did. Null while the surface is still open, and after a cancel.</summary>
    public CaptureRect? Result { get; private set; }

    /// <summary>Whether the surface is finished with — confirmed or cancelled, which the window watches to close itself.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>
    /// Starts a drag, unless the press landed where no display is. A staggered arrangement leaves the capture
    /// with area the compositor never painted, and offering it as though it were screen is the one thing the
    /// surface must not do — those pixels were nobody's.
    /// </summary>
    public bool BeginDrag(double surfaceX, double surfaceY)
    {
        var point = ToImagePixel(surfaceX, surfaceY);

        // Asked in the image's own space, because that is the space the point is in. DisplayAt takes a desktop
        // point and would answer against DesktopBounds — which on a scaled display is the smaller rectangle, so
        // everything past its width would read as "no display" and refuse a perfectly ordinary drag.
        if (_capture.ToDesktopPoint(point) is null)
        {
            return false;
        }

        _anchor = point;
        Selection = new CaptureRect(point.X, point.Y, 0, 0);
        return true;
    }

    /// <summary>Extends the drag. The anchor stays put, so dragging up or left is the same gesture as down or right.</summary>
    public void DragTo(double surfaceX, double surfaceY)
    {
        if (_anchor is not { } anchor)
        {
            return;
        }

        var point = _Clamp(ToImagePixel(surfaceX, surfaceY));
        Selection = new CaptureRect(
            Math.Min(anchor.X, point.X),
            Math.Min(anchor.Y, point.Y),
            Math.Abs(point.X - anchor.X),
            Math.Abs(point.Y - anchor.Y));
    }

    /// <summary>Ends the drag. A press that never moved leaves a rectangle with no area, which is not a selection.</summary>
    public void EndDrag()
    {
        _anchor = null;
        if (Selection is { Width: 0 } or { Height: 0 })
        {
            Selection = null;
        }
    }

    /// <summary>Everything, in one press — the whole capture, gaps and all, since that is what was on the screens.</summary>
    public void SelectEverything() => Selection = new CaptureRect(0, 0, ImageWidth, ImageHeight);

    /// <summary>
    /// Whether picking a window is available at all (AC-330). False on a desktop that will not say where its
    /// windows are — Wayland, deliberately — and the surface shows that rather than offering a mode that
    /// silently does nothing, which is the failure AC-220 was rejected for.
    /// </summary>
    public bool CanPickWindow => _windows is not null;

    /// <summary>
    /// What the surface tells the operator it can do. Window picking is named only where it exists, and its
    /// absence is said out loud rather than left as a key that does nothing — the failure AC-220 was rejected
    /// for was a mode that looked available and was not.
    /// </summary>
    public string Hint =>
        "Drag a region · Arrow keys nudge, Shift resizes, Ctrl for larger steps · A takes everything · "
        + (CanPickWindow
            ? "W picks a window · "
            : "Picking a window is not something this desktop will allow · ")
        + "Enter confirms · Esc cancels";

    /// <summary>The window the pointer is over, once <see cref="PickingWindow"/> is on. Null over the desktop, or where windows cannot be asked about.</summary>
    [ObservableProperty]
    private DesktopWindow? _hoveredWindow;

    /// <summary>Whether the surface is highlighting whole windows rather than waiting for a drag.</summary>
    [ObservableProperty]
    private bool _pickingWindow;

    /// <summary>Turns window picking on, if this desktop can do it. Off again puts the surface back to dragging a region.</summary>
    public void PickWindows(bool picking)
    {
        PickingWindow = picking && CanPickWindow;
        if (!PickingWindow)
        {
            HoveredWindow = null;
        }
    }

    /// <summary>
    /// Highlights the front-most window under the pointer, and marks it out. Since the capture already holds
    /// every pixel on the desktop, taking a window is a crop to its rectangle — there is no second capture, and
    /// nothing is asked of the window itself.
    /// </summary>
    public void HoverAt(double surfaceX, double surfaceY)
    {
        if (_windows is not { } windows || !PickingWindow)
        {
            return;
        }

        var point = ToImagePixel(surfaceX, surfaceY);

        // First match wins because the list is front to back, which is what makes an overlapped window pick the
        // one on top rather than the one enumerated first.
        var found = windows.FirstOrDefault(candidate => candidate.ImageBounds.Contains(point));
        HoveredWindow = found.Window;
        Selection = found.Window is null ? null : found.ImageBounds;
    }

    /// <summary>
    /// Each window's rectangle in the image's own pixels, worked out once against the capture that was taken.
    /// A window off the edge of every display — minimised, or on a screen that is not in this capture — has no
    /// pixels here and is dropped rather than mapped to a corner.
    /// </summary>
    private IReadOnlyList<(DesktopWindow Window, CaptureRect ImageBounds)> _InImageSpace(IReadOnlyList<DesktopWindow> windows) =>
        windows
            .Select(window => (Window: window, Image: _ToImageBounds(window.Bounds)))
            .Where(mapped => mapped.Image is not null)
            .Select(mapped => (mapped.Window, mapped.Image!.Value))
            .ToList();

    /// <summary>
    /// A window's rectangle in the image, built from the part of it each display actually holds. Mapping its two
    /// corners straight through would drop any window that hangs over the edge of the captured desktop — half
    /// off the side of a screen, or across the gap a staggered arrangement leaves — even though the rest of it is
    /// plainly visible and croppable.
    /// </summary>
    private CaptureRect? _ToImageBounds(CaptureRect desktop)
    {
        CaptureRect? bounds = null;
        foreach (var display in _capture.Displays)
        {
            if (_Overlap(desktop, display.DesktopBounds) is not { } shared)
            {
                continue;
            }

            // The far corner is asked for one position inside the overlap and then put back, because the
            // rectangle is half-open: the position at Right belongs to whatever is beside the window, and on a
            // scaled display asking for it lands on the next display or on nothing at all.
            var topLeft = display.ToImagePixel(new CapturePoint(shared.X, shared.Y));
            var bottomRight = display.ToImagePixel(new CapturePoint(shared.Right - 1, shared.Bottom - 1));
            var piece = new CaptureRect(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X + 1, bottomRight.Y - topLeft.Y + 1);

            bounds = bounds is { } covered ? _Union(covered, piece) : piece;
        }

        return bounds;
    }

    private static CaptureRect? _Overlap(CaptureRect first, CaptureRect second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);

        return right > left && bottom > top ? new CaptureRect(left, top, right - left, bottom - top) : null;
    }

    private static CaptureRect _Union(CaptureRect first, CaptureRect second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);

        return new CaptureRect(left, top, Math.Max(first.Right, second.Right) - left, Math.Max(first.Bottom, second.Bottom) - top);
    }

    /// <summary>
    /// Moves the selection by whole image pixels, or resizes its far corner when <paramref name="resize"/> is
    /// set. One image pixel, not one of the window's units: on a scaled display those are not the same distance,
    /// and a nudge that moved by a logical unit could not reach every pixel at all — which is the entire reason
    /// the keys exist.
    /// </summary>
    public void Nudge(int dx, int dy, bool resize = false, int step = 1)
    {
        if (Selection is not { } selection)
        {
            return;
        }

        Selection = resize
            ? selection with
            {
                Width = Math.Clamp(selection.Width + (dx * step), 1, ImageWidth - selection.X),
                Height = Math.Clamp(selection.Height + (dy * step), 1, ImageHeight - selection.Y),
            }
            : selection with
            {
                X = Math.Clamp(selection.X + (dx * step), 0, ImageWidth - selection.Width),
                Y = Math.Clamp(selection.Y + (dy * step), 0, ImageHeight - selection.Height),
            };
    }

    /// <summary>Takes what is marked out. A surface with nothing on it confirms nothing rather than sending the whole desktop by accident.</summary>
    public void Confirm()
    {
        if (Selection is { Width: > 0, Height: > 0 })
        {
            Result = Selection;
            IsClosed = true;
        }
    }

    /// <summary>
    /// Gives up. Nothing is injected and nothing is said — pressing Escape is the ordinary way to change your
    /// mind, and a toast for it would be nagging (the rule AC-220 already settled).
    /// </summary>
    public void Cancel()
    {
        Result = null;
        IsClosed = true;
    }

    /// <summary>Where a point on the window falls in the image, through the one ratio everything here goes by.</summary>
    public CapturePoint ToImagePixel(double surfaceX, double surfaceY) =>
        new(
            (int)Math.Floor(surfaceX * _RatioX),
            (int)Math.Floor(surfaceY * _RatioY));

    /// <summary>Where a rectangle of the image sits on the window — the way back, for drawing what is selected.</summary>
    public (double X, double Y, double Width, double Height) ToSurface(CaptureRect region) =>
        (region.X / _RatioX, region.Y / _RatioY, region.Width / _RatioX, region.Height / _RatioY);

    // Guarded because the view sets the surface size after construction, and a division by an unlaid-out window
    // would put every early pointer event on pixel zero rather than nowhere.
    private double _RatioX => SurfaceWidth > 0 ? ImageWidth / SurfaceWidth : 1;

    private double _RatioY => SurfaceHeight > 0 ? ImageHeight / SurfaceHeight : 1;

    private bool _Fits(CaptureRect region) =>
        region is { Width: > 0, Height: > 0 } && region.X >= 0 && region.Y >= 0
        && region.Right <= ImageWidth && region.Bottom <= ImageHeight;

    private CapturePoint _Clamp(CapturePoint point) =>
        new(Math.Clamp(point.X, 0, ImageWidth), Math.Clamp(point.Y, 0, ImageHeight));
}
