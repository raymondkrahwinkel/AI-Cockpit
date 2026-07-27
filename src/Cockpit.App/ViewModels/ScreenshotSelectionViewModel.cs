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
    /// <summary>
    /// How thick a frame is drawn, in the captured image's pixels. Fixed rather than scaled to the mark: a thin
    /// line on a large screenshot is what the operator drew it to avoid, and a frame that got heavier the smaller
    /// it was would swallow what it is pointing at.
    /// </summary>
    private const int OutlineThickness = 4;

    /// <summary>
    /// How thick an arrow's shaft is drawn at its thinnest, in the captured image's pixels. Heavier than a frame
    /// because it is read as a shape rather than as a border: a frame is understood from the rectangle it
    /// encloses, while an arrow has only itself to be seen by.
    /// </summary>
    private const int ArrowThickness = 6;

    private readonly ScreenCapture _capture;
    private readonly List<Mark> _marks = [];
    private readonly IReadOnlyList<(DesktopWindow Window, CaptureRect ImageBounds)>? _windows;
    private readonly uint _markColour;
    private readonly Func<CaptureRect, int>? _brightnessUnder;
    private CapturePoint? _anchor;

    /// <param name="markColour">
    /// What a mark is drawn in, as 0xAARRGGBB. Handed in without a default on purpose: the accent lives in the
    /// theme, which is the view's to read, and a default here would be a second copy of a colour that is supposed
    /// to have exactly one home — the mistake AC-334 spent a ticket undoing.
    /// </param>
    /// <param name="brightnessUnder">
    /// How light the capture is inside a rectangle, 0 to 255. A wash has to know, because ink over paper and ink
    /// over a terminal have to move the pixels in opposite directions (AC-361), and only the picture can say which
    /// of the two this is. Handed in because the decoded picture is the view's — this class holds the arithmetic,
    /// not the pixels.
    /// <para>
    /// Left out, a wash darkens, which is what a marker pen does and what is right for the documents these are
    /// mostly dragged over. It is a fallback rather than a preference: a surface that cannot look at its own
    /// picture cannot tell a terminal from a page, and over a terminal this one is close to invisible.
    /// </para>
    /// </param>
    public ScreenshotSelectionViewModel(
        ScreenCapture capture,
        int imageWidth,
        int imageHeight,
        uint markColour,
        CaptureRect? lastRegion = null,
        IDesktopWindows? windows = null,
        Func<CaptureRect, int>? brightnessUnder = null)
    {
        _capture = capture;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        _markColour = markColour;
        _brightnessUnder = brightnessUnder;

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
    [NotifyPropertyChangedFor(nameof(TakingEverything))]
    [NotifyPropertyChangedFor(nameof(DraggingRegion))]
    private CaptureRect? _selection;

    /// <summary>How wide the window is drawing the image, in whatever units it lays out in. Set by the view once it knows.</summary>
    [ObservableProperty]
    private double _surfaceWidth;

    /// <summary>How tall the window is drawing the image.</summary>
    [ObservableProperty]
    private double _surfaceHeight;

    /// <summary>What the operator settled on, once they did. Null while the surface is still open, and after a cancel.</summary>
    public ScreenshotSelection? Result { get; private set; }

    /// <summary>
    /// What has been placed on the capture, in its own pixels, in the order it was placed (AC-359). Held here
    /// rather than drawn on top, because these are applied to the pixels that get sent — an overlay that could
    /// travel separately from the image is a redaction that one day will not.
    /// </summary>
    public IReadOnlyList<Mark> Marks => _marks;

    /// <summary>
    /// Which mark tool is in hand, or nothing while the surface is choosing what to take instead. One value
    /// rather than a flag per tool: they share a drag, so two of them being on at once has no meaning.
    /// </summary>
    public MarkTool? MarkingWith { get; private set; }

    /// <summary>Whether the surface is drawing boxes to hide rather than choosing what to take.</summary>
    public bool Redacting => MarkingWith == MarkTool.Redaction;

    /// <summary>Whether the surface is drawing frames around what the model should look at.</summary>
    public bool Outlining => MarkingWith == MarkTool.Outline;

    /// <summary>Whether the surface is drawing arrows at the one thing the model should look at.</summary>
    public bool Pointing => MarkingWith == MarkTool.Arrow;

    /// <summary>Whether the surface is washing bands of colour over what the model should read rather than skim.</summary>
    public bool Highlighting => MarkingWith == MarkTool.Highlight;

    /// <summary>
    /// Whether the surface is standing on what taking everything left behind: the whole capture marked out, and
    /// no other tool chosen since. Both halves are needed. Without the selection it would survive a drag that
    /// replaced it; without the flag, pressing Region here would light nothing, because everything is a region
    /// too — and a tool that does not answer being pressed is what marking the resting one was added to stop.
    /// </summary>
    public bool TakingEverything => _tookEverything && Selection == new CaptureRect(0, 0, ImageWidth, ImageHeight);

    /// <summary>
    /// Whether the pointer is doing the ordinary thing — dragging out a region. The resting state said as a
    /// property of its own, so the control panel can mark it the same way it marks the other two rather than
    /// leaving the one you are actually in as the only unlit button (AC-358).
    /// </summary>
    /// <remarks>
    /// Taking everything is subtracted so that exactly one tool is ever lit: two at once stops answering the
    /// question the row is there for. It is safe to subtract because choosing this tool clears it — the operator
    /// who presses Region while everything is marked has said which tool is in hand, and gets told so.
    /// </remarks>
    public bool DraggingRegion => !PickingWindow && MarkingWith is null && !TakingEverything;

    // Set by taking everything, cleared by choosing any other tool. What the selection is cannot answer this on
    // its own: the whole capture is a perfectly ordinary region to be standing in with the region tool.
    private bool _tookEverything;

    /// <summary>
    /// What the window tool says when you hover it — including, where this desktop will not allow it, why it is
    /// greyed out. A disabled control that says nothing is the failure AC-220 was rejected for, one layer down.
    /// </summary>
    public string WindowToolTip => CanPickWindow
        ? "Take a whole window: click the one you want"
        : "Picking a window is not something this desktop will allow — it will not say where other applications' windows are";

    /// <summary>
    /// Turns redaction on, which needs something to redact — there is nothing to hide until a region has been
    /// marked out, and boxes drawn over the whole desktop would have nowhere to end up.
    /// </summary>
    public void Redact(bool redacting) => MarkWith(MarkTool.Redaction, redacting);

    /// <summary>Turns frame-drawing on, which needs a region for the same reason redaction does.</summary>
    public void Outline(bool outlining) => MarkWith(MarkTool.Outline, outlining);

    /// <summary>Turns arrow-drawing on. Same condition as the others: an arrow pointing at something outside what is being sent points at nothing.</summary>
    public void Point(bool pointing) => MarkWith(MarkTool.Arrow, pointing);

    /// <summary>Turns the wash on, on the same condition as the rest — there is nothing to emphasise until something is being sent.</summary>
    public void Highlight(bool highlighting) => MarkWith(MarkTool.Highlight, highlighting);

    /// <summary>
    /// Takes a mark tool up or puts it down. Every tool that marks needs something to mark on — a frame around
    /// the whole desktop and a box over it both have nowhere to end up, since what is sent is the region.
    /// </summary>
    public void MarkWith(MarkTool tool, bool marking)
    {
        var canMark = marking && Selection is { Width: > 0, Height: > 0 };

        MarkingWith = canMark ? tool : MarkingWith == tool ? null : MarkingWith;
        MarkingNeedsARegion = marking && !canMark;
        if (canMark)
        {
            PickWindows(false);
            _StopTakingEverything();
        }

        _SaidTheModeChanged();
    }

    /// <summary>
    /// Takes back the last mark, whatever it was (AC-359) — one stack for the lot, because two undo histories on
    /// one surface is two things to keep straight while the picture is the only thing worth looking at.
    /// </summary>
    /// <remarks>
    /// Only the last, and there is no redo. A mark is one drag, so putting it back costs the gesture that made
    /// it; a redo stack, meanwhile, has to be dropped the moment a new mark is placed, and getting that wrong
    /// brings back a redaction the operator took away. That failure is a leak, not an inconvenience.
    /// </remarks>
    public void Undo()
    {
        if (_marks.Count > 0)
        {
            _marks.RemoveAt(_marks.Count - 1);
            OnPropertyChanged(nameof(Marks));
        }
    }

    private void _SaidTheModeChanged()
    {
        OnPropertyChanged(nameof(MarkingWith));
        OnPropertyChanged(nameof(Redacting));
        OnPropertyChanged(nameof(Outlining));
        OnPropertyChanged(nameof(Pointing));
        OnPropertyChanged(nameof(Highlighting));
        OnPropertyChanged(nameof(DraggingRegion));
        OnPropertyChanged(nameof(Hint));
    }

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
        if (MarkingWith is not null)
        {
            // Anchored without the display check the region drag makes: a mark only ever goes on a region that
            // was already chosen on a display, so there is nothing here that could be nobody's pixels.
            _anchor = point;
            return true;
        }


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
        if (MarkingWith is not null)
        {
            PendingTo = point;
            return;
        }

        Selection = new CaptureRect(
            Math.Min(anchor.X, point.X),
            Math.Min(anchor.Y, point.Y),
            Math.Abs(point.X - anchor.X),
            Math.Abs(point.Y - anchor.Y));
    }

    /// <summary>Ends the drag. A press that never moved leaves a rectangle with no area, which is not a selection.</summary>
    public void EndDrag()
    {
        var anchor = _anchor;
        _anchor = null;
        if (MarkingWith is { } tool)
        {
            // The same call the preview was built from, so what is kept is exactly the shape that was on screen
            // a moment ago rather than a second construction of it from the same two points.
            if (anchor is { } from && PendingTo is { } to && _MarkOf(tool, from, to) is { } mark)
            {
                _marks.Add(mark);
                OnPropertyChanged(nameof(Marks));
            }

            PendingTo = null;
            return;
        }

        if (Selection is { Width: 0 } or { Height: 0 })
        {
            Selection = null;
        }
    }

    /// <summary>
    /// Where the mark being drawn right now has got to, so the surface can show it before it is let go of. The
    /// point rather than the rectangle it would make: an arrow drawn up and to the left and one drawn down and to
    /// the right cover the same rectangle and are opposite marks, so a rectangle cannot be what a drag is held as.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingMarkPreview))]
    private CapturePoint? _pendingTo;

    /// <summary>
    /// That drag as the mark it is about to become, or nothing when none is under way — or when what has been
    /// dragged so far would not be a mark. Built here rather than in the surface so the preview cannot end up
    /// being a different kind of thing from what gets placed.
    /// </summary>
    public Mark? PendingMarkPreview =>
        MarkingWith is { } tool && _anchor is { } from && PendingTo is { } to ? _MarkOf(tool, from, to) : null;

    /// <summary>
    /// The mark a drag from one point to another makes with the tool in hand, or nothing where that drag has no
    /// extent. What counts as no extent is the kind's own business: a box or a frame needs area, while an arrow
    /// needs only to have gone somewhere — a tall thin one is a perfectly good arrow and a rectangle of no width.
    /// </summary>
    private Mark? _MarkOf(MarkTool tool, CapturePoint from, CapturePoint to) => tool switch
    {
        MarkTool.Redaction => _Between(from, to) is { Width: > 0, Height: > 0 } box ? new RedactionMark(box) : null,
        MarkTool.Outline => _Between(from, to) is { Width: > 0, Height: > 0 } frame
            ? new OutlineMark(frame, _markColour, OutlineThickness)
            : null,
        MarkTool.Arrow => from == to ? null : new ArrowMark(from, to, _markColour, ArrowThickness),
        MarkTool.Highlight => _Between(from, to) is { Width: > 0, Height: > 0 } band
            ? new HighlightMark(band, _markColour, _BlendFor(band))
            : null,
        _ => throw new NotSupportedException($"There is no mark for {tool}."),
    };

    /// <summary>
    /// Which way a wash over that band has to move the pixels: down into them where they are light, up out of them
    /// where they are dark. Decided once, when the band is placed, rather than every time it is drawn — the same
    /// wash is drawn on the surface and into the delivered picture, and two decisions could disagree.
    /// </summary>
    private HighlightBlend _BlendFor(CaptureRect band) =>
        _brightnessUnder?.Invoke(band) is { } brightness && brightness < 128
            ? HighlightBlend.Lighten
            : HighlightBlend.Darken;

    private static CaptureRect _Between(CapturePoint anchor, CapturePoint point) =>
        new(
            Math.Min(anchor.X, point.X),
            Math.Min(anchor.Y, point.Y),
            Math.Abs(point.X - anchor.X),
            Math.Abs(point.Y - anchor.Y));

    /// <summary>Everything, in one press — the whole capture, gaps and all, since that is what was on the screens.</summary>
    /// <remarks>
    /// Says so out loud rather than leaving it to the selection changing. Coming back here from a mark tool, the
    /// selection is <em>already</em> the whole capture — nothing changed, so nothing was raised, and the row stayed
    /// dark while everything was taken. That is the dead button AC-358 exists to have got rid of, one path along.
    /// <para>
    /// Whichever mark tool was in hand is put down too, the way taking one up stops this from being lit. The row
    /// lights exactly one tool, and it cannot do that if the two sides disagree about who clears whom.
    /// </para>
    /// </remarks>
    public void SelectEverything()
    {
        if (MarkingWith is { } tool)
        {
            MarkWith(tool, false);
        }

        _tookEverything = true;
        Selection = new CaptureRect(0, 0, ImageWidth, ImageHeight);
        OnPropertyChanged(nameof(TakingEverything));
        OnPropertyChanged(nameof(DraggingRegion));
    }

    /// <summary>
    /// Back to dragging out a region, from whichever tool was in hand — including from having taken everything,
    /// which leaves what is marked out alone and only says which tool the next drag belongs to.
    /// </summary>
    /// <remarks>
    /// Whatever mark tool is in hand is put down, rather than one named kind of it. This asked to put down
    /// redaction by name until AC-360, from when redaction was the only tool there was to be holding — so pressing
    /// Region while drawing frames left you drawing frames, and the row went on saying so.
    /// </remarks>
    public void ChooseRegion()
    {
        PickWindows(false);
        if (MarkingWith is { } tool)
        {
            MarkWith(tool, false);
        }

        _StopTakingEverything();
    }

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
    public string Hint => this switch
    {
        { Redacting: true } =>
            "Drag over anything that should not be sent · Ctrl+Z takes back the last mark · Enter confirms · Esc cancels",
        { Outlining: true } =>
            "Drag a frame around what the model should look at · Ctrl+Z takes back the last mark · Enter confirms · Esc cancels",
        // Said as where-to-where rather than as "drag an arrow", because which end gets the head is the one thing
        // about this tool that cannot be guessed from looking at it before you have used it once.
        { Pointing: true } =>
            "Drag from where the arrow starts to what it should point at · Ctrl+Z takes back the last mark · Enter confirms · Esc cancels",
        // Says what it does not do, because that is the whole difference from the tool beside it: a band that
        // covered what it marked would be the box that hides, drawn in a lighter colour.
        { Highlighting: true } =>
            "Drag a band over what should be read rather than skimmed — it stays legible · Ctrl+Z takes back the last mark · Enter confirms · Esc cancels",
        { PickingWindow: true } =>
            "Click the window you want · W goes back to dragging a region · Esc cancels",
        // Said as a refusal rather than left silent: pressing a mark tool with nothing marked out used to do
        // nothing at all, which reads exactly like a key that is not wired up.
        { MarkingNeedsARegion: true } =>
            "Mark out a region first — the marking tools go on what you are sending · Esc cancels",
        // What the tools do is on the tools, keys and all, so this says only what has no button: the drag itself,
        // the arrows, and the two keys that end it.
        _ =>
            "Drag a region, or double-click one to take it · Arrow keys nudge, Shift resizes, Ctrl for larger steps · "
            + (CanPickWindow ? "" : "Picking a window is not something this desktop will allow · ")
            + "Enter confirms · Esc cancels",
    };

    /// <summary>Whether a mark tool was asked for while there was nothing to mark on — which is why nothing happened.</summary>
    public bool MarkingNeedsARegion { get; private set; }

    /// <summary>The window the pointer is over, once <see cref="PickingWindow"/> is on. Null over the desktop, or where windows cannot be asked about.</summary>
    [ObservableProperty]
    private DesktopWindow? _hoveredWindow;

    /// <summary>Whether the surface is highlighting whole windows rather than waiting for a drag.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraggingRegion))]
    private bool _pickingWindow;

    /// <summary>Turns window picking on, if this desktop can do it. Off again puts the surface back to dragging a region.</summary>
    public void PickWindows(bool picking)
    {
        PickingWindow = picking && CanPickWindow;
        if (!PickingWindow)
        {
            HoveredWindow = null;
        }
        else
        {
            _StopTakingEverything();
        }

        OnPropertyChanged(nameof(Hint));
    }

    /// <summary>Another tool is in hand now, so the row stops marking the one that took everything.</summary>
    private void _StopTakingEverything()
    {
        _tookEverything = false;
        OnPropertyChanged(nameof(TakingEverything));
        OnPropertyChanged(nameof(DraggingRegion));
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
            if (desktop.Overlap(display.DesktopBounds) is not { } shared)
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
        // A drag that is still in progress is finished first. Enter can arrive while the button is down — the
        // keyboard and the mouse are used together — and a box that only lives in PendingRedaction until
        // EndDrag would otherwise be dropped silently, sending the very region it was drawn to hide.
        if (_anchor is not null)
        {
            EndDrag();
        }

        if (Selection is not { Width: > 0, Height: > 0 } region)
        {
            return;
        }

        // Moved into the crop's own space here rather than when they were drawn: the operator draws on the whole
        // capture, and what is sent is the crop — so a mark has to be told where it sits in the picture that
        // actually leaves the machine, which is not known until the region is settled. Each kind does its own
        // clipping, because they do not survive the edge the same way: a box loses the part that is outside, a
        // frame keeps its shape and simply has that side fall off the picture.
        Result = new ScreenshotSelection
        {
            Region = region,
            Marks = _marks.Select(mark => mark.ClipTo(region)).OfType<Mark>().ToList(),
        };
        IsClosed = true;
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

    /// <summary>
    /// The display a point on the window falls on, as its rectangle in the image's pixels — or nothing where the
    /// point is in the gap a staggered arrangement leaves. The control panel is put on it rather than on the
    /// window, because the window spans every screen at once and its middle is a place nobody is looking (AC-358).
    /// </summary>
    public CaptureRect? DisplayAt(double surfaceX, double surfaceY)
    {
        var point = ToImagePixel(surfaceX, surfaceY);

        return _capture.Displays.FirstOrDefault(display => display.ImageBounds.Contains(point))?.ImageBounds;
    }

    /// <summary>Where a point on the window falls in the image, through the one ratio everything here goes by.</summary>
    public CapturePoint ToImagePixel(double surfaceX, double surfaceY) =>
        new(
            (int)Math.Floor(surfaceX * _RatioX),
            (int)Math.Floor(surfaceY * _RatioY));

    /// <summary>
    /// Where a corner of a mark's shape sits on the window. Kept in fractions on the way out as well as on the
    /// way in: rounding here would put the arrow's barbs on whole window units, which on a scaled display are
    /// further apart than the image's own pixels — so the preview would be the blunter shape of the two.
    /// </summary>
    public MarkPoint ToSurface(MarkPoint point) => new(point.X / _RatioX, point.Y / _RatioY);

    /// <summary>
    /// How wide a line of that many image pixels is on the window. A stroke has one width where the surface has
    /// two ratios, so this takes the horizontal one — they are the same number whenever the window covers the
    /// desktop at a single scale, which is the arrangement it is built for, and where a mixed-DPI window comes
    /// out the wrong size a mark is drawn a hair off rather than in the wrong place.
    /// </summary>
    /// <remarks>
    /// Without this the preview would be a different picture from the one that gets sent: a mark's thickness is
    /// in the image's pixels, and drawing that number as window units makes it twice too heavy on a display
    /// scaled by two — the operator checks a frame that is thicker than the one they are about to hand over.
    /// </remarks>
    public double ToSurfaceLength(double imagePixels) => imagePixels / _RatioX;

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
