using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
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
    private bool _wasActivated;

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
    public static async Task<CaptureRect?> PickAsync(ScreenCapture capture, CaptureRect? lastRegion, IDesktopWindows windows, Window owner)
    {
        using var stream = new MemoryStream(capture.Image);
        var bitmap = new Bitmap(stream);

        var window = new ScreenshotSelectionWindow();
        var selection = new ScreenshotSelectionViewModel(capture, bitmap.PixelSize.Width, bitmap.PixelSize.Height, lastRegion, windows);
        window._selection = selection;
        window._bitmap = bitmap;
        window.DataContext = selection;
        window.Capture.Source = bitmap;
        window._Cover(owner.Screens);

        // Shown rather than ShowDialog'd. A modal needs a visible owner, and the cockpit's main window is often
        // not one: closing it minimises to tray by default, and the global hotkey is exactly the key an operator
        // presses while the cockpit is out of the way. This surface owns the screen for as long as it is up
        // anyway, so it has nothing to gain from being modal to a window that may be hidden.
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();

        await closed.Task;
        return selection.Result;
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

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

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
                selection.PickWindows(false);
                selection.SelectEverything();
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
        ReadoutText.Text = $"{region.Width} × {region.Height}";
        Readout.IsVisible = true;
        Canvas.SetLeft(Readout, x);
        Canvas.SetTop(Readout, Math.Max(0, y - 24));
    }

    private static void _Place(Shape shape, double x, double y, double width, double height)
    {
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        shape.Width = Math.Max(0, width);
        shape.Height = Math.Max(0, height);
    }

}
