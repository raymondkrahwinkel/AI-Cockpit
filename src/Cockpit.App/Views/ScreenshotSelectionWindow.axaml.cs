using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.App.Theming;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Screenshots;

// The shape, not the file system's. Both are in scope here through the implicit usings, and this file draws.
using Path = Avalonia.Controls.Shapes.Path;

namespace Cockpit.App.Views;

/// <summary>
/// The selection surface (AC-329): one undecorated window over the whole virtual desktop, showing the capture,
/// with a rectangle dragged on it. Deliberately thin — every number it works with comes from
/// <see cref="ScreenshotSelectionViewModel"/>, which is where the arithmetic can be held to a test.
/// </summary>
/// <remarks>
/// One window spanning every display rather than one per screen. Avalonia issue #16128 has a fullscreen window
/// on KDE Plasma stretching across all monitors instead of staying on one; that is filed against the XWayland
/// path this app is on, so spanning everything is what it already does — and doing so on purpose means the
/// conversion is window size against image size, with the per-screen scaling factors XRandR gets wrong under
/// fractional scaling (KDE bug 502390) never entering into it.
/// </remarks>
public partial class ScreenshotSelectionWindow : Window
{
    private ScreenshotSelectionViewModel? _selection;
    private Bitmap? _bitmap;

    /// <summary>How far the control panels sit from the edge of the display they are on.</summary>
    private const double ControlsMargin = 24;

    /// <summary>How much air is left between the two panels while they are still stacked where they were put.</summary>
    private const double PanelGap = 10;

    /// <summary>
    /// The panels the operator has moved themselves. Those stop following the pointer's display: having been put
    /// somewhere by hand is the strongest statement about where a panel belongs that this surface can receive.
    /// </summary>
    private readonly HashSet<Control> _movedByHand = [];

    /// <summary>The panel being dragged and where it was gripped, or nothing when none is.</summary>
    private (Control Panel, Point Grip)? _panelDrag;

    /// <summary>
    /// What stands in for each mark on the canvas, one per mark, in the order they were placed. Added as they are
    /// drawn and kept afterwards.
    /// </summary>
    /// <remarks>
    /// Controls rather than shapes since AC-361: a wash is the one mark that is not drawn but blended, and in
    /// Avalonia only an image carries a blend mode. A rectangle painted at a fraction of its strength would be a
    /// different picture from the one that gets sent.
    /// </remarks>
    private readonly List<Control> _shapes = [];
    private bool _wasActivated;

    /// <summary>Where the pointer was last seen, in the window's units. The control panel follows the display it is on.</summary>
    private Point _pointer;

    public ScreenshotSelectionWindow()
    {
        InitializeComponent();
        Activated += (_, _) => _wasActivated = true;
        Deactivated += _OnDeactivated;
    }

    /// <summary>
    /// Puts the surface over the desktop the capture came off and waits for the operator, handing back the
    /// region they marked out in the image's own pixels — or nothing, if they changed their mind.
    /// </summary>
    public static async Task<ScreenshotSelection?> PickAsync(ScreenCapture capture, CaptureRect? lastRegion, IDesktopWindows windows, Window owner)
    {
        using var stream = new MemoryStream(capture.Image);
        var bitmap = new Bitmap(stream);

        var window = Build(capture, bitmap, lastRegion, windows);
        window._Cover(owner.Screens);

        // Shown rather than ShowDialog'd. A modal needs a visible owner, and the cockpit's main window is often
        // not one: closing it minimises to tray by default, and the global hotkey is exactly the key an operator
        // presses while the cockpit is out of the way. This surface owns the screen for as long as it is up
        // anyway, so it has nothing to gain from being modal to a window that may be hidden.
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();

        await closed.Task;
        return window._selection?.Result;
    }

    /// <summary>
    /// The surface built and wired, without being put on screen. Its own step because everything here runs
    /// before anything is shown, and it is where the window touches the controls its XAML declares — which is
    /// exactly what a test can reach and what nothing was reaching.
    /// </summary>
    internal static ScreenshotSelectionWindow Build(
        ScreenCapture capture, Bitmap bitmap, CaptureRect? lastRegion, IDesktopWindows windows)
    {
        var window = new ScreenshotSelectionWindow
        {
            _selection = new ScreenshotSelectionViewModel(
                capture, bitmap.PixelSize.Width, bitmap.PixelSize.Height, _AccentColour(), lastRegion, windows,
                area => _BrightnessIn(bitmap, area)),
            _bitmap = bitmap,
        };

        window.DataContext = window._selection;
        window.Capture.Source = bitmap;

        return window;
    }

    /// <summary>
    /// How light the capture is inside a rectangle, 0 to 255 — what a wash needs in order to know whether it is
    /// ink over paper or ink over a terminal (AC-361).
    /// </summary>
    /// <remarks>
    /// Read off the middle of the band rather than all of it. A highlight is dragged over one piece of text, so
    /// its middle is what it is about; copying every row of a band the width of a 4K screen would be megabytes
    /// moved to answer one question with a yes or a no in it.
    /// </remarks>
    private static int _BrightnessIn(Bitmap bitmap, CaptureRect area)
    {
        const int sample = 32;

        var width = Math.Clamp(Math.Min(area.Width, sample), 1, bitmap.PixelSize.Width);
        var height = Math.Clamp(Math.Min(area.Height, sample), 1, bitmap.PixelSize.Height);
        var left = Math.Clamp(area.X + ((area.Width - width) / 2), 0, bitmap.PixelSize.Width - width);
        var top = Math.Clamp(area.Y + ((area.Height - height) / 2), 0, bitmap.PixelSize.Height - height);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(left, top, width, height), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        long total = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            // The first three channels whichever way round they sit: their sum is the same in BGRA as in RGBA,
            // and only the alpha has to stay out of it.
            total += pixels[index] + pixels[index + 1] + pixels[index + 2];
        }

        return (int)(total / (pixels.Length / 4 * 3));
    }

    /// <summary>
    /// The accent, as a number the imaging library can take. Read here because the theme is the view's to know:
    /// a frame is burnt into the picture by Infrastructure, which has no business holding a copy of a colour
    /// whose one home is <c>Theme.axaml</c>.
    /// </summary>
    private static uint _AccentColour() =>
        ThemeBrush.Resolve("CockpitAccentBrush", "#3b82f6") is ISolidColorBrush solid
            ? solid.Color.ToUInt32()
            : throw new InvalidOperationException("The accent is not a solid colour, so a frame has nothing to be drawn in.");

    /// <summary>
    /// Puts the window over every screen. The rectangle comes from Avalonia's own screen list rather than from
    /// the capture's <see cref="CapturedDisplay.DesktopBounds"/>, because those are not in one space across
    /// platforms — device pixels on Windows, the compositor's logical layout under Wayland — while a window's
    /// position and size have fixed, and different, units of their own.
    /// </summary>
    /// <remarks>
    /// <see cref="Window.Position"/> is in physical pixels and <see cref="Layoutable.Width"/> in logical units,
    /// so the size is divided by the scaling of the screen the window starts on — the same conversion
    /// <c>VoiceOverlayWindow</c> makes, in the other direction. On a mixed-DPI desktop one factor cannot be
    /// right for every screen at once; nothing in Avalonia's window model can express that, and the selection
    /// arithmetic above works off the window's actual laid-out size rather than this, so a window that came out
    /// the wrong size is visibly wrong rather than quietly cropping the wrong region.
    /// </remarks>
    private void _Cover(Screens screens)
    {
        var bounds = screens.All
            .Select(screen => screen.Bounds)
            .Aggregate((covered, screen) => covered.Union(screen));
        var scaling = screens.ScreenFromPoint(bounds.Position)?.Scaling ?? 1;

        Position = bounds.Position;
        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;
    }

    /// <summary>
    /// Gives up when the screens change underneath it. The surface is a frozen picture of a desktop that no
    /// longer exists the moment a monitor is unplugged or its resolution changes, and every coordinate on it
    /// then points somewhere else — better to take nothing than to crop by a map of the wrong place.
    /// </summary>
    private void _OnScreensChanged(object? sender, EventArgs e)
    {
        if (_selection is { IsClosed: false } selection)
        {
            selection.Cancel();
            Close();
        }
    }

    /// <summary>Lets go of the decoded capture. It is a desktop's worth of pixels wrapped around native memory, and one is decoded per capture.</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Screens.Changed -= _OnScreensChanged;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Screens.Changed += _OnScreensChanged;
        _Measure();

        // Focused explicitly: the keys are half of this surface, and a window that opens without focus swallows
        // the first Escape — which is the one an operator who opened it by accident reaches for.
        Focus();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _Measure();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_selection is not { } selection)
        {
            return;
        }

        // A press anywhere on a panel belongs to that panel, not to the picture. Self included: the padding and
        // the gaps between the rows have no child control to catch them, so a press there resolves to the panel
        // itself — and that is a good part of what an operator sees as the panel.
        if (_PanelUnder(e.Source) is { } panel)
        {
            // And picks it up. A press on a tool never arrives here at all — a button handles its own press, so
            // it does not reach the window — which is what leaves pressing a tool as pressing a tool rather than
            // as the start of a drag. Everything that does arrive is padding, a gap between rows, a label or the
            // panel itself, and all of those are things an operator would call "the panel".
            _panelDrag = (panel, e.GetPosition(panel));
            e.Pointer.Capture(this);
            return;
        }

        // In window mode the press is the confirmation, not the start of a drag. Falling through to BeginDrag
        // would put a zero-size rectangle where the highlighted window was, and EndDrag would then clear it —
        // so the click that is meant to take the window is exactly what threw it away.
        if (selection.PickingWindow)
        {
            selection.Confirm();
            if (selection.IsClosed)
            {
                Close();
            }

            return;
        }

        // A second click inside what is already marked out takes it, the way a double-click accepts a choice
        // everywhere else. Only inside: outside it is the start of a new region, which is what the first click
        // of the pair already began.
        // Not while a mark tool is in hand. Two quick marks in the same spot are two marks — reading the second as
        // "take it" would hand over a shot the operator was still working on, and with a note it would fire on the
        // ordinary act of clicking the same place twice.
        if (e.ClickCount == 2 && selection.MarkingWith is null && selection.Selection is { } marked
            && marked.Contains(selection.ToImagePixel(e.GetPosition(Surface).X, e.GetPosition(Surface).Y)))
        {
            selection.Confirm();
            if (selection.IsClosed)
            {
                Close();
            }

            return;
        }

        if (selection.BeginDrag(e.GetPosition(Surface).X, e.GetPosition(Surface).Y))
        {
            _Draw();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selection is not { } selection)
        {
            return;
        }

        _pointer = e.GetPosition(Surface);

        // A panel being moved takes the pointer entirely. Nothing else may read this move: the picture underneath
        // is not being marked, no window is being hovered, and the panels are certainly not to be re-placed on the
        // display the pointer happens to have crossed into while carrying one.
        if (_panelDrag is { } moving)
        {
            _movedByHand.Add(moving.Panel);
            _Put(moving.Panel, _pointer.X - moving.Grip.X, _pointer.Y - moving.Grip.Y);
            return;
        }

        // Placed on every move, not only on the ones that redraw the selection. A panel follows the display the
        // pointer is on, and moving between screens without a button down is exactly how an operator gets there —
        // so leaving it to the drag and window-mode branches below left it on whichever screen the surface opened.
        _PlaceControls();

        // Window mode first: the button may well be down — an operator holding it while moving is still
        // pointing at windows, and treating that as a drag would replace the highlight with a rectangle of
        // their own by accident.
        if (selection.PickingWindow)
        {
            selection.HoverAt(e.GetPosition(Surface).X, e.GetPosition(Surface).Y);
            _Draw();
        }
        else if (e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed)
        {
            selection.DragTo(e.GetPosition(Surface).X, e.GetPosition(Surface).Y);
            _Draw();
        }
    }

    /// <summary>
    /// The panel a press landed on, or nothing where it landed on the picture. Self and ancestors, because the
    /// padding between the tools has no child control of its own and a press there resolves to the panel.
    /// </summary>
    private Border? _PanelUnder(object? source) =>
        source is Visual pressed && pressed.GetSelfAndVisualAncestors().ToList() is { } chain
            ? chain.Contains(Controls) ? Controls : chain.Contains(MarkControls) ? MarkControls : null
            : null;

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // Letting go of a panel ends only that. Falling through to EndDrag would finish a mark the operator never
        // began — the press that started this was on the panel, so there is no drag on the picture to end.
        if (_panelDrag is not null)
        {
            _panelDrag = null;
            e.Pointer.Capture(null);
            return;
        }

        if (_selection is { PickingWindow: false })
        {
            _selection.EndDrag();
            _Draw();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_selection is not { } selection)
        {
            return;
        }

        // Every key below is a shortcut, and while a note is open every key is a letter instead. Answered here,
        // before any of them, and answered for *all* of them rather than for the ones that look dangerous: an
        // operator typing "Windows" would otherwise pick a window, blank the region and take the shot.
        if (selection.Typing)
        {
            _WhileTyping(selection, e);
            return;
        }

        var step = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 10 : 1;
        var resize = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.Escape:
                selection.Cancel();
                break;
            case Key.Enter:
                selection.Confirm();
                break;
            case Key.A:
                _ChooseEverything(selection);
                break;
            case Key.B:
                // Boxes are a mode too: the same drag either marks out what to take or what to hide, and the
                // operator says which before moving the pointer.
                selection.Redact(!selection.Redacting);
                break;
            case Key.O:
                selection.Outline(!selection.Outlining);
                break;
            case Key.T:
                selection.Label(!selection.Labelling);
                break;
            case Key.D:
                selection.Draw(!selection.Drawing);
                break;
            case Key.H:
                selection.Highlight(!selection.Highlighting);
                break;
            case Key.P:
                // P for pointing, not A for arrow: A takes the whole capture and had it first (AC-358), and a
                // key that moved to make room for a later tool would break the one thing the panel promises —
                // that what it says is what the keyboard does.
                selection.Point(!selection.Pointing);
                break;
            case Key.R:
                // The way back to the ordinary drag. W and B toggle, so it was always reachable by pressing the
                // one you were in again — but only if you knew which that was, which is what this epic is about.
                selection.ChooseRegion();
                break;
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                selection.Undo();
                break;
            case Key.W:
                // Window picking is a mode rather than a click target: the pointer is already the selection's,
                // so the operator says which of the two it means before moving it.
                selection.PickWindows(!selection.PickingWindow);
                break;
            case Key.Left:
                selection.Nudge(-1, 0, resize, step);
                break;
            case Key.Right:
                selection.Nudge(1, 0, resize, step);
                break;
            case Key.Up:
                selection.Nudge(0, -1, resize, step);
                break;
            case Key.Down:
                selection.Nudge(0, 1, resize, step);
                break;
            default:
                return;
        }

        e.Handled = true;
        if (selection.IsClosed)
        {
            Close();
            return;
        }

        _Draw();
    }

    /// <summary>
    /// The three keys that mean something while a note is open. Everything else is marked handled and dropped
    /// here — the characters themselves arrive as text input, not as keys, so nothing is lost by it.
    /// </summary>
    /// <remarks>
    /// Escape closes the note rather than the surface, and a second Escape then cancels as it always did: the
    /// operator who wants out presses it twice, and the one who wants their note keeps it by pressing it once.
    /// Enter does the same as Escape rather than confirming the capture, for the same reason — a note is finished
    /// before a shot is taken, and the alternative is a label that is typed and then thrown away by the key that
    /// ends it.
    /// </remarks>
    private void _WhileTyping(ScreenshotSelectionViewModel selection, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Enter:
                selection.FinishTyping();
                break;
            case Key.Back:
                selection.Backspace();
                break;
        }

        e.Handled = true;
        _Draw();
    }

    /// <summary>
    /// What the operator typed, while a note is open. Taken from text input rather than from keys, so that what
    /// lands in the note is what their keyboard layout actually produces.
    /// </summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_selection is not { Typing: true } selection || e.Text is not { Length: > 0 } typed)
        {
            return;
        }

        selection.Type(typed);
        e.Handled = true;
        _Draw();
    }

    /// <summary>
    /// Gives up when the surface loses the desktop's attention. One left behind is worse than none: it covers
    /// every screen, takes every key, and says nothing about what it is.
    /// </summary>
    /// <remarks>
    /// Only after it has been activated once. A window that is deactivated before it was ever active is a
    /// platform ordering detail, not the operator alt-tabbing away — and acting on it would close the surface
    /// the instant it opened, on whichever platform happens to raise the events that way round.
    /// </remarks>
    private void _OnDeactivated(object? sender, EventArgs e)
    {
        if (!_wasActivated || _selection is not { IsClosed: false } selection)
        {
            return;
        }

        selection.Cancel();
        Close();
    }

    /// <summary>
    /// The tools, chosen with the mouse (AC-358). Each makes exactly the call its key makes and is followed by
    /// the same redraw — a button that did something subtly different from the key beside it would be worse than
    /// no button at all.
    /// </summary>
    private void _OnRegionTool(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseRegion());

    private void _OnWindowTool(object? sender, RoutedEventArgs e) =>
        _Tool(selection => selection.PickWindows(!selection.PickingWindow));

    private void _OnEverythingTool(object? sender, RoutedEventArgs e) => _Tool(_ChooseEverything);

    private void _OnOutlineTool(object? sender, RoutedEventArgs e) =>
        _Tool(selection => selection.Outline(!selection.Outlining));

    /// <summary>
    /// The inks and the line weights (AC-375). Each is the same shape of call as a tool, and each redraws — the
    /// mark being dragged right now is previewed in what has just been chosen.
    /// </summary>
    private void _OnInkAccent(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseInk(_AccentColour()));

    private void _OnInkRed(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseInk(MarkInk.Red));

    private void _OnInkYellow(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseInk(MarkInk.Yellow));

    private void _OnInkGreen(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseInk(MarkInk.Green));

    private void _OnInkWhite(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseInk(MarkInk.White));

    private void _OnWeightThin(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseWeight(MarkWeight.Thin));

    private void _OnWeightMedium(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseWeight(MarkWeight.Medium));

    private void _OnWeightThick(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseWeight(MarkWeight.Thick));

    /// <summary>
    /// Paints the swatches in the inks they stand for, and marks the chosen ink and weight. Restated on every draw
    /// rather than bound, because which one is on is one value against eight controls — eight bindings, each of
    /// which would have to be told what the other seven mean.
    /// </summary>
    private void _ShowPalette(ScreenshotSelectionViewModel selection)
    {
        var inks = new (Button Button, Ellipse Dot, uint Colour)[]
        {
            (InkAccent, InkAccentDot, _AccentColour()),
            (InkRed, InkRedDot, MarkInk.Red),
            (InkYellow, InkYellowDot, MarkInk.Yellow),
            (InkGreen, InkGreenDot, MarkInk.Green),
            (InkWhite, InkWhiteDot, MarkInk.White),
        };

        foreach (var (button, dot, colour) in inks)
        {
            dot.Fill = new SolidColorBrush(Color.FromUInt32(colour));
            button.Classes.Set("active", selection.MarkColour == colour);
        }

        WeightThin.Classes.Set("active", selection.Weight == MarkWeight.Thin);
        WeightMedium.Classes.Set("active", selection.Weight == MarkWeight.Medium);
        WeightThick.Classes.Set("active", selection.Weight == MarkWeight.Thick);
    }

    private void _OnLabelTool(object? sender, RoutedEventArgs e) =>
        _Tool(selection => selection.Label(!selection.Labelling));

    private void _OnDrawTool(object? sender, RoutedEventArgs e) =>
        _Tool(selection => selection.Draw(!selection.Drawing));

    private void _OnHighlightTool(object? sender, RoutedEventArgs e) =>
        _Tool(selection => selection.Highlight(!selection.Highlighting));

    private void _OnArrowTool(object? sender, RoutedEventArgs e) =>
        _Tool(selection => selection.Point(!selection.Pointing));

    private void _OnRedactTool(object? sender, RoutedEventArgs e) =>
        _Tool(selection => selection.Redact(!selection.Redacting));

    private void _Tool(Action<ScreenshotSelectionViewModel> choose)
    {
        if (_selection is not { } selection)
        {
            return;
        }

        choose(selection);
        _Draw();
    }

    /// <summary>
    /// The whole capture in one press. Named here rather than written out twice so the button and the key it
    /// carries cannot drift apart — the one thing this panel promises is that the two are the same surface said
    /// twice. Window mode comes off first: what it marks out is a window, and taking everything is not that.
    /// </summary>
    private static void _ChooseEverything(ScreenshotSelectionViewModel selection)
    {
        selection.PickWindows(false);
        selection.SelectEverything();
    }

    /// <summary>
    /// Puts both panels at the top of the display the pointer is on, one under the other. The window spans every
    /// screen at once, so its own middle is a spot nobody is looking at; the display under the pointer is the one
    /// they are.
    /// </summary>
    /// <remarks>
    /// They follow the pointer's display until the operator moves one by hand, and that one then stops — the drag
    /// <em>is</em> the memory of where it should be. An earlier version of this stepped aside on its own for
    /// whatever was being marked out (AC-358), and nothing remembered where it had been, so every reason to move
    /// away became a reason to move back the moment it lapsed and the row rocked under the operator's hand. A
    /// panel that moves because it was pulled there has no such argument with itself.
    /// </remarks>
    private void _PlaceControls()
    {
        if (_selection is not { } selection)
        {
            return;
        }

        // Left where it was when the pointer is on no display at all — the gap a staggered arrangement leaves.
        // Centring on the whole window would put a panel in that gap, which is the one place with no screen
        // behind it.
        if (selection.DisplayAt(_pointer.X, _pointer.Y) is not { } bounds)
        {
            return;
        }

        var display = _ToRect(selection.ToSurface(bounds));
        var top = display.Y + ControlsMargin;

        foreach (var panel in new[] { Controls, MarkControls })
        {
            // Bounds until it has been arranged once, DesiredSize before that — the first placement happens as
            // the window opens, and a panel measured at nothing would be pinned to the corner it started in.
            var size = panel.Bounds.Width > 0 ? panel.Bounds.Size : panel.DesiredSize;
            if (size.Width <= 0)
            {
                return;
            }

            if (!_movedByHand.Contains(panel))
            {
                _Put(panel, display.X + ((display.Width - size.Width) / 2), top);
            }

            // Stacked from the one above whether or not it moved, so a panel left where it was does not end up
            // under one that was dragged away from over it.
            top = Canvas.GetTop(panel) + (_movedByHand.Contains(panel) ? 0 : size.Height + PanelGap);
        }
    }

    /// <summary>
    /// Puts a panel at a place on the surface, clamped so that all of it stays reachable. Clamped against the
    /// window rather than the display: a screen narrower or shorter than the panel would otherwise push it off
    /// the edge, and a panel half outside the window is a tool you cannot press.
    /// </summary>
    private void _Put(Control panel, double left, double top)
    {
        var size = panel.Bounds.Width > 0 ? panel.Bounds.Size : panel.DesiredSize;

        Canvas.SetLeft(panel, Math.Clamp(left, 0, Math.Max(0, Surface.Bounds.Width - size.Width)));
        Canvas.SetTop(panel, Math.Clamp(top, 0, Math.Max(0, Surface.Bounds.Height - size.Height)));
    }

    private static Rect _ToRect((double X, double Y, double Width, double Height) area) =>
        new(area.X, area.Y, area.Width, area.Height);

    private void _Measure()
    {
        if (_selection is not { } selection)
        {
            return;
        }

        selection.SurfaceWidth = Surface.Bounds.Width;
        selection.SurfaceHeight = Surface.Bounds.Height;
        _Draw();
    }

    private void _Draw()
    {
        if (_selection is not { } selection)
        {
            return;
        }

        _PlaceControls();
        _ShowPalette(selection);

        var width = Surface.Bounds.Width;
        var height = Surface.Bounds.Height;
        if (selection.Selection is not { } region)
        {
            _Place(Marquee, 0, 0, 0, 0);
            _Place(ShadeTop, 0, 0, width, height);
            _Place(ShadeBottom, 0, 0, 0, 0);
            _Place(ShadeLeft, 0, 0, 0, 0);
            _Place(ShadeRight, 0, 0, 0, 0);
            Readout.IsVisible = false;
            return;
        }

        var (x, y, w, h) = selection.ToSurface(region);
        _Place(Marquee, x, y, w, h);
        _Place(ShadeTop, 0, 0, width, y);
        _Place(ShadeBottom, 0, y + h, width, Math.Max(0, height - (y + h)));
        _Place(ShadeLeft, 0, y, x, h);
        _Place(ShadeRight, x + w, y, Math.Max(0, width - (x + w)), h);

        // The size is reported in the image's pixels, which is what the session receives — the window's units
        // would be a different number on a scaled display and would read as a lie next to the attachment.
        _DrawMarks(selection);
        ReadoutText.Text = $"{region.Width} × {region.Height}";
        Readout.IsVisible = true;
        Canvas.SetLeft(Readout, x);
        Canvas.SetTop(Readout, Math.Max(0, y - 24));
    }

    /// <summary>
    /// Shows what has been marked, before the operator commits to sending it — including the one still being
    /// dragged. A redaction is drawn solid rather than as a border: what they are checking is that nothing
    /// readable is left, and a frame around legible text would say the opposite of what the box is going to do.
    /// </summary>
    /// <remarks>
    /// Drawn from the same list, in the same order, that gets burnt in — so what is on screen is a preview of the
    /// picture rather than a second opinion about it.
    /// </remarks>
    private void _DrawMarks(ScreenshotSelectionViewModel selection)
    {
        var drawn = selection.PendingMarkPreview is { } pending
            ? selection.Marks.Append(pending).ToList()
            : selection.Marks;

        for (var index = 0; index < drawn.Count; index++)
        {
            _Show(_ShapeAt(index, drawn[index]), drawn[index], selection);
        }

        for (var index = drawn.Count; index < _shapes.Count; index++)
        {
            // Kept rather than removed: an undo is very often followed by another mark, and a handful of
            // emptied shapes costs nothing next to rebuilding the canvas on every pointer move. Emptied and not
            // merely resized — a path draws the geometry it holds whatever it was told its size was, so a
            // rectangle shrunk to nothing disappears and an arrow shrunk to nothing does not.
            _Empty(_shapes[index]);
        }
    }

    /// <summary>
    /// The shape standing in for the mark at that position, made afresh where the kind there has changed. A frame
    /// is a rectangle and an arrow is a path, and no amount of restyling turns one into the other — where an undo
    /// leaves a different kind of mark at an index, the shape has to be replaced rather than repainted.
    /// </summary>
    private Control _ShapeAt(int index, Mark mark)
    {
        if (index < _shapes.Count && _Suits(_shapes[index], mark))
        {
            return _shapes[index];
        }

        var shape = _StandInFor(mark);
        if (index < _shapes.Count)
        {
            Shade.Children[Shade.Children.IndexOf(_shapes[index])] = shape;
            _shapes[index] = shape;
        }
        else
        {
            _shapes.Add(shape);
            Shade.Children.Insert(Shade.Children.IndexOf(Marquee), shape);
        }

        return shape;
    }

    /// <summary>What a mark of that kind has to be drawn with. A frame is a rectangle, an arrow is a path, and a wash is an image because that is the only thing in Avalonia that carries a blend mode.</summary>
    private static Control _StandInFor(Mark mark) => mark switch
    {
        ArrowMark => new Path(),
        HighlightMark => new Image { Stretch = Stretch.Fill },
        StrokeMark => new Path(),
        // A plate with letters on it, which is what the mark is: the letters need one known background, and the
        // plate is the only part of the picture underneath that can be relied on.
        TextMark => new Border { Child = new TextBlock() },
        _ => new Rectangle(),
    };

    /// <summary>Whether a kept control is still the right thing for the mark now at its position.</summary>
    private static bool _Suits(Control shape, Mark mark) => (shape, mark) switch
    {
        (Path, ArrowMark or StrokeMark) => true,
        (Image, HighlightMark) => true,
        (Border, TextMark) => true,
        (Rectangle, RedactionMark or OutlineMark) => true,
        _ => false,
    };

    /// <summary>
    /// Makes one shape look like the mark it is standing in for, and puts it where that mark is. Restated on every
    /// draw rather than set when the shape is made, because the shapes are kept and reused as marks come and go —
    /// one that was a redaction a moment ago has to stop looking like one.
    /// </summary>
    /// <remarks>
    /// Every thickness goes through the surface conversion rather than being used as it stands. A mark's thickness
    /// is in the image's pixels; drawn as window units it comes out heavier than what will be burnt in by exactly
    /// the display's scale, and the preview stops being a preview.
    /// </remarks>
    private void _Show(Control shape, Mark mark, ScreenshotSelectionViewModel selection)
    {
        switch (mark, shape)
        {
            case (RedactionMark redaction, Rectangle box):
                box.Fill = Marquee.Stroke;
                box.Stroke = null;
                box.Opacity = 0.85;
                _Place(box, selection.ToSurface(redaction.Area));
                break;
            case (OutlineMark outline, Rectangle frame):
                frame.Fill = null;
                frame.Stroke = new SolidColorBrush(Color.FromUInt32(outline.Colour));
                frame.StrokeThickness = selection.ToSurfaceLength(outline.Thickness);
                frame.Opacity = 1;
                _Place(frame, selection.ToSurface(outline.Area));
                break;
            case (ArrowMark arrow, Path drawn):
                drawn.Fill = new SolidColorBrush(Color.FromUInt32(arrow.Colour));
                drawn.Stroke = null;
                drawn.Opacity = 1;
                _Trace(drawn, arrow, selection);
                break;
            case (HighlightMark highlight, Image wash):
                // A one-pixel picture of the colour, stretched over the band. An image is what carries a blend
                // mode here, and the blend is the tool: painted on as a translucent rectangle instead, the wash
                // would drag the text and the page under it towards each other and cost most of their contrast.
                wash.Source = _OnePixelOf(highlight.Wash);
                wash.BlendMode = highlight.Blend == HighlightBlend.Darken
                    ? BitmapBlendingMode.Multiply
                    : BitmapBlendingMode.Screen;
                _Place(wash, selection.ToSurface(highlight.Area));
                break;
            case (TextMark note, Border plate):
                plate.Background = new SolidColorBrush(Color.FromUInt32(note.Plate));
                plate.CornerRadius = new CornerRadius(selection.ToSurfaceLength(note.Padding / 2));
                plate.Padding = new Thickness(selection.ToSurfaceLength(note.Padding));
                ((TextBlock)plate.Child!).Text = note.Text;
                ((TextBlock)plate.Child!).FontSize = selection.ToSurfaceLength(note.Size);
                ((TextBlock)plate.Child!).Foreground = new SolidColorBrush(Color.FromUInt32(note.Colour));

                // Sized by what is in it rather than to a rectangle: how wide a label is, is how wide its letters
                // came out, and that is not known until the font has drawn them.
                var (left, top, _, _) = selection.ToSurface(new CaptureRect(note.At.X, note.At.Y, 0, 0));
                Canvas.SetLeft(plate, left);
                Canvas.SetTop(plate, top);
                plate.Width = double.NaN;
                plate.Height = double.NaN;
                break;
            case (StrokeMark stroke, Path drawn):
                _Trace(drawn, stroke, selection);
                break;
            default:
                throw new NotSupportedException(
                    $"There is no way to show a {mark.GetType().Name} with a {shape.GetType().Name}.");
        }
    }

    /// <summary>
    /// Lays the arrow's own outline into a path, in the window's units. The corners are the mark's — the same list
    /// the imaging library fills — so the shape on screen and the shape in the delivered picture are one shape
    /// converted twice rather than two shapes worked out twice.
    /// </summary>
    /// <remarks>
    /// The geometry is written relative to the shape's top-left corner and the shape is then placed there, rather
    /// than written in the surface's coordinates and placed at the origin. A path laid out at its own absolute
    /// position measures as though it began at zero, and the empty space in front of it becomes part of its size.
    /// </remarks>
    private static void _Trace(Path path, ArrowMark arrow, ScreenshotSelectionViewModel selection)
    {
        if (arrow.Silhouette() is not { Count: > 0 } corners)
        {
            _Empty(path);
            return;
        }

        var onSurface = corners.Select(selection.ToSurface).ToList();
        var margin = selection.ToSurfaceLength(1);
        var left = onSurface.Min(corner => corner.X) - margin;
        var top = onSurface.Min(corner => corner.Y) - margin;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(onSurface[0].X - left, onSurface[0].Y - top), isFilled: true);
            foreach (var corner in onSurface.Skip(1))
            {
                context.LineTo(new Point(corner.X - left, corner.Y - top));
            }

            context.EndFigure(isClosed: true);
        }

        path.Data = geometry;
        Canvas.SetLeft(path, left);
        Canvas.SetTop(path, top);

        // Left to the geometry rather than sized: a path told how big to be stretches its shape to fit, which
        // would bend the head by whatever the rounding of the box came to.
        path.Width = double.NaN;
        path.Height = double.NaN;
    }

    /// <summary>
    /// Lays the freehand line into the path that stands in for it, in the window's units, from the same curve the
    /// imaging library draws.
    /// </summary>
    /// <remarks>
    /// It took a pair of paths until AC-375 — a wider ring underneath and the line over it — because a line cannot
    /// be drawn and ringed at once the way a filled shape can. The ring is gone with the palette, and the second
    /// path went with it.
    /// </remarks>
    private static void _Trace(Path drawn, StrokeMark stroke, ScreenshotSelectionViewModel selection)
    {
        if (stroke.Start() is not { } start || stroke.Curve() is not { Count: > 0 } curves
            || stroke.Bounds() is not { } bounds)
        {
            _Empty(drawn);
            return;
        }

        var (left, top, width, height) = selection.ToSurface(bounds);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var from = selection.ToSurface(start);
            context.BeginFigure(new Point(from.X - left, from.Y - top), isFilled: false);
            foreach (var curve in curves)
            {
                context.CubicBezierTo(
                    _At(selection, curve.FirstControl, left, top),
                    _At(selection, curve.SecondControl, left, top),
                    _At(selection, curve.End, left, top));
            }

            context.EndFigure(isClosed: false);
        }

        drawn.Data = geometry;
        drawn.Fill = null;
        drawn.Stroke = new SolidColorBrush(Color.FromUInt32(stroke.Colour));
        drawn.StrokeThickness = selection.ToSurfaceLength(stroke.Thickness);
        drawn.StrokeLineCap = PenLineCap.Round;
        drawn.StrokeJoin = PenLineJoin.Round;
        _Place(drawn, left, top, width, height);
    }

    private static Point _At(ScreenshotSelectionViewModel selection, MarkPoint point, double left, double top)
    {
        var onSurface = selection.ToSurface(point);

        return new Point(onSurface.X - left, onSurface.Y - top);
    }

    private static void _Place(Control shape, (double X, double Y, double Width, double Height) area) =>
        _Place(shape, area.X, area.Y, area.Width, area.Height);

    private static void _Place(Control shape, double x, double y, double width, double height)
    {
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        shape.Width = Math.Max(0, width);
        shape.Height = Math.Max(0, height);
    }

    /// <summary>
    /// One pixel of a colour, to be stretched over a band. A real bitmap rather than a drawing of a filled
    /// rectangle: the blend mode belongs to the image, and a drawing is composited before it ever gets there.
    /// </summary>
    private static WriteableBitmap _OnePixelOf(uint colour)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(1, 1), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using var buffer = bitmap.Lock();

        // Blue, green, red, alpha in memory order — which little-endian reads back as the 0xAARRGGBB the mark
        // carries, so the value goes in as it stands.
        Marshal.WriteInt32(buffer.Address, unchecked((int)colour));

        return bitmap;
    }

    /// <summary>Leaves a kept shape drawing nothing — its size taken away, and, where it holds one, its geometry too.</summary>
    private static void _Empty(Control shape)
    {
        switch (shape)
        {
            case Path path:
                path.Data = null;
                break;
            case Panel panel:
                foreach (var child in panel.Children.OfType<Path>())
                {
                    child.Data = null;
                }

                break;
        }

        _Place(shape, 0, 0, 0, 0);
    }

}
