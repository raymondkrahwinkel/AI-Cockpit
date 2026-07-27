using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Screenshots;

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

    /// <summary>How far the control panel sits from the edge of the display it is on.</summary>
    private const double ControlsMargin = 24;

    /// <summary>The rectangles standing in for the redaction boxes, one per box, added to the canvas as they are drawn.</summary>
    private readonly List<Rectangle> _boxes = [];
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
            _selection = new ScreenshotSelectionViewModel(capture, bitmap.PixelSize.Width, bitmap.PixelSize.Height, lastRegion, windows),
            _bitmap = bitmap,
        };

        window.DataContext = window._selection;
        window.Capture.Source = bitmap;

        return window;
    }

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

        // A press anywhere on the control panel belongs to the panel, not to the picture. Self included: the
        // padding and the gaps between the rows have no child control to catch them, so a press there resolves to
        // the panel itself — and that is a good part of what an operator sees as the panel.
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().Contains(Controls))
        {
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
        if (e.ClickCount == 2 && selection.Selection is { } marked
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

        // Placed on every move, not only on the ones that redraw the selection. The panel follows the display the
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

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
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
            case Key.R:
                // The way back to the ordinary drag. W and B toggle, so it was always reachable by pressing the
                // one you were in again — but only if you knew which that was, which is what this epic is about.
                selection.ChooseRegion();
                break;
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                selection.UndoRedaction();
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
    /// Puts the control panel at the top of the display the pointer is on. The window spans every screen at once,
    /// so its own middle is a spot nobody is looking at; the display under the pointer is the one they are.
    /// </summary>
    /// <remarks>
    /// It stays there — it does not step aside for what is being marked out, though an earlier version of this did
    /// (AC-358). Nothing here remembers where the panel was, so every reason to move away became a reason to move
    /// back the moment it lapsed, and the row rocked between the two edges while the operator was trying to use
    /// it. A tool that moves while you are reaching for it costs more than one that sits over the picture, and the
    /// picture is frozen anyway. The price, said plainly: a drag cannot be *started* on the strip the panel
    /// occupies, since a press there belongs to the panel — dragging through it and letting go past it is fine.
    /// </remarks>
    private void _PlaceControls()
    {
        if (_selection is not { } selection)
        {
            return;
        }

        // Bounds until it has been arranged once, DesiredSize before that — the first placement happens as the
        // window opens, and a panel measured at nothing would be pinned to the corner it started in.
        var size = Controls.Bounds.Width > 0 ? Controls.Bounds.Size : Controls.DesiredSize;
        if (size.Width <= 0)
        {
            return;
        }

        // Left where it was when the pointer is on no display at all — the gap a staggered arrangement leaves.
        // Centring on the whole window would put the panel in that gap, which is the one place with no screen
        // behind it.
        if (selection.DisplayAt(_pointer.X, _pointer.Y) is not { } bounds)
        {
            return;
        }

        var display = _ToRect(selection.ToSurface(bounds));
        var left = display.X + ((display.Width - size.Width) / 2);
        var top = display.Y + ControlsMargin;

        // Clamped last, against the window rather than the display: a screen narrower or shorter than the panel
        // would otherwise push it off the edge, and a panel half outside the window is a tool you cannot press.
        Canvas.SetLeft(Controls, Math.Clamp(left, 0, Math.Max(0, Surface.Bounds.Width - size.Width)));
        Canvas.SetTop(Controls, Math.Clamp(top, 0, Math.Max(0, Surface.Bounds.Height - size.Height)));
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
        _DrawRedactions(selection);
        ReadoutText.Text = $"{region.Width} × {region.Height}";
        Readout.IsVisible = true;
        Canvas.SetLeft(Readout, x);
        Canvas.SetTop(Readout, Math.Max(0, y - 24));
    }

    /// <summary>
    /// Shows which areas will be obscured, before the operator commits to sending it. Solid rather than an
    /// outline: what they are checking is that nothing readable is left, and a border around legible text would
    /// say the opposite of what the box is going to do.
    /// </summary>
    private void _DrawRedactions(ScreenshotSelectionViewModel selection)
    {
        var drawn = selection.PendingRedaction is { } pending
            ? selection.Redactions.Append(pending).ToList()
            : selection.Redactions;

        while (_boxes.Count < drawn.Count)
        {
            var box = new Rectangle { Fill = Marquee.Stroke, Opacity = 0.85 };
            _boxes.Add(box);
            Shade.Children.Insert(Shade.Children.IndexOf(Marquee), box);
        }

        for (var index = 0; index < _boxes.Count; index++)
        {
            if (index < drawn.Count)
            {
                var (x, y, width, height) = selection.ToSurface(drawn[index]);
                _Place(_boxes[index], x, y, width, height);
            }
            else
            {
                // Kept rather than removed: an undo is very often followed by another box, and a handful of
                // zero-sized rectangles costs nothing next to rebuilding the canvas on every pointer move.
                _Place(_boxes[index], 0, 0, 0, 0);
            }
        }
    }

    private static void _Place(Shape shape, double x, double y, double width, double height)
    {
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        shape.Width = Math.Max(0, width);
        shape.Height = Math.Max(0, height);
    }

}
