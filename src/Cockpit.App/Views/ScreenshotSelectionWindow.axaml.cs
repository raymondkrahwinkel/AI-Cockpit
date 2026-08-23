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

// AC-329: undecorated selection window over the whole virtual desktop; every number comes from
// ScreenshotSelectionViewModel, kept testable. One window spans every display (Avalonia #16128
// already does this on KDE/XWayland) rather than per-screen, avoiding XRandR's scaling errors (KDE bug 502390).
public partial class ScreenshotSelectionWindow : Window
{
    private ScreenshotSelectionViewModel? _selection;
    private Bitmap? _bitmap;

    // How far the control panels sit from the edge of the display they are on.
    private const double ControlsMargin = 24;

    // How much air is left between the two panels while they are still stacked where they were put.
    private const double PanelGap = 10;

    // The panels the operator has moved themselves. Those stop following the pointer's display: having been put
    // somewhere by hand is the strongest statement about where a panel belongs that this surface can receive.
    private readonly HashSet<Control> _movedByHand = [];

    // The panel being dragged and where it was gripped, or nothing when none is.
    private (Control Panel, Point Grip)? _panelDrag;

    // One stand-in per mark, in placement order, added as drawn and kept afterwards. Controls, not
    // shapes, since AC-361: a wash is blended not drawn, and only an Avalonia image carries a blend mode.
    private readonly List<Control> _shapes = [];
    private bool _wasActivated;

    // Where the pointer was last seen, in the window's units. The control panel follows the display it is on.
    private Point _pointer;

    // AC-566: the gate a confirm passes through before this window closes, or null if the setting
    // is off. Set once before Show, so all three confirm paths go through _Confirm instead of closing themselves.
    internal Func<ScreenshotSelection, Window, Task<bool>>? PreviewGate { get; set; }

    public ScreenshotSelectionWindow()
    {
        InitializeComponent();
        Activated += (_, _) => _wasActivated = true;
        Deactivated += _OnDeactivated;
        TypingTarget.TextChanged += _OnTypedTextChanged;
    }

    // Puts the surface over the desktop the capture came off and waits for the operator, handing back the
    // region they marked out in the image's own pixels — or nothing, if they changed their mind.
    public static async Task<ScreenshotSelection?> PickAsync(
        ScreenCapture capture, CaptureRect? lastRegion, IDesktopWindows windows, Window owner,
        Func<ScreenshotSelection, Window, Task<bool>>? previewGate = null)
    {
        using var stream = new MemoryStream(capture.Image);
        var bitmap = new Bitmap(stream);

        var window = Build(capture, bitmap, lastRegion, windows);
        window.PreviewGate = previewGate;
        window._Cover(owner.Screens);

        // Shown, not ShowDialog'd: a modal needs a visible owner, and the cockpit's main window is
        // often minimised to tray when the global hotkey fires. This surface owns the screen anyway.
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();

        await closed.Task;
        return window._selection?.Result;
    }

    // Whether a confirm is already on its way through the preview gate: the gap between Confirm()
    // and the gate's dialog opening spans an awaited settings load and crop-and-burn while the
    // window still has focus, so a second Enter/click must not start a second confirm.
    private bool _confirming;

    // Confirms, and — if a preview gate is set — asks it before this window actually closes (AC-566). The one
    // point all three ways to confirm run through, so none of them can end up bypassing it. A decline reopens
    // the surface exactly as it was: nothing here has touched the selection or its marks.
    private async void _Confirm(ScreenshotSelectionViewModel selection)
    {
        if (_confirming)
        {
            return;
        }

        _confirming = true;
        try
        {
            selection.Confirm();
            if (!selection.IsClosed)
            {
                return;
            }

            if (selection.Result is { } result && PreviewGate is { } gate)
            {
                var approved = false;
                try
                {
                    approved = await gate(result, this).ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // The preview is a courtesy on top of the confirm, not the confirm itself — a gate that fails
                    // to even ask must not eat a selection the operator already marked out. Enter still confirms,
                    // and reaches this same method.
                }

                if (!approved)
                {
                    selection.ReopenAfterDeclinedPreview();
                    return;
                }
            }

            Close();
        }
        finally
        {
            _confirming = false;
        }
    }

    // The surface built and wired, without being put on screen. Its own step because everything here runs
    // before anything is shown, and it is where the window touches the controls its XAML declares — which is
    // exactly what a test can reach and what nothing was reaching.
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

    // AC-361: how light the capture is inside a rectangle (0-255), so a wash knows ink-over-paper
    // vs ink-over-terminal. Read off the band's middle row, not all of it — cheaper for a yes/no answer.
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

    // The accent, as a number the imaging library can take. Read here because the theme is the view's to know:
    // a frame is burnt into the picture by Infrastructure, which has no business holding a copy of a colour
    // whose one home is `Theme.axaml`.
    private static uint _AccentColour() =>
        ThemeBrush.Resolve("CockpitAccentBrush", "#2563eb") is ISolidColorBrush solid
            ? solid.Color.ToUInt32()
            : throw new InvalidOperationException("The accent is not a solid colour, so a frame has nothing to be drawn in.");

    // Puts the window over every screen, using Avalonia's screen list, not CapturedDisplay.DesktopBounds
    // (not one space across platforms). Window.Position is physical, Layoutable.Width logical, so
    // size divides by the starting screen's scaling (VoiceOverlayWindow's conversion, reversed).
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

    // Gives up when the screens change underneath it. The surface is a frozen picture of a desktop that no
    // longer exists the moment a monitor is unplugged or its resolution changes, and every coordinate on it
    // then points somewhere else — better to take nothing than to crop by a map of the wrong place.
    private void _OnScreensChanged(object? sender, EventArgs e)
    {
        if (_selection is { IsClosed: false } selection)
        {
            selection.Cancel();
            Close();
        }
    }

    // Lets go of the decoded capture. It is a desktop's worth of pixels wrapped around native memory, and one is decoded per capture.
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

        // Focused explicitly, else the window swallows the first Escape. Must be Focusable: text
        // needs a focused element, and every control here is deliberately unfocusable so a tool click never costs the keyboard.
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
            // And picks it up. A tool press never arrives here — its Button handles its own press —
            // so pressing a tool stays pressing a tool, not the start of a panel drag.
            _panelDrag = (panel, e.GetPosition(panel));
            e.Pointer.Capture(this);
            return;
        }

        // In window mode the press is the confirmation, not the start of a drag. Falling through to BeginDrag
        // would put a zero-size rectangle where the highlighted window was, and EndDrag would then clear it —
        // so the click that is meant to take the window is exactly what threw it away.
        if (selection.PickingWindow)
        {
            _Confirm(selection);
            return;
        }

        // A second click inside the marked-out region takes it, like a double-click accepts a
        // choice elsewhere; outside starts a new region instead. Not while a mark tool is in hand —
        // two quick marks in the same spot are two marks, not "take it".
        if (e.ClickCount == 2 && selection.MarkingWith is null && selection.Selection is { } marked
            && marked.Contains(selection.ToImagePixel(e.GetPosition(Surface).X, e.GetPosition(Surface).Y)))
        {
            _Confirm(selection);
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
        else
        {
            // Only while nothing is held down — a cursor that changed mid-drag would say something other than
            // what the drag already committed to doing.
            _UpdateCursor(selection, e.GetPosition(Surface));
        }
    }

    // AC-565: shows what a press right now would do — resize over a grip, move inside the
    // selection, else the ordinary cross — including modes where a grip cursor would promise a
    // drag the surface won't honour.
    private void _UpdateCursor(ScreenshotSelectionViewModel selection, Point pointer)
    {
        if (selection.DraggingRegion)
        {
            if (selection.GripAt(pointer.X, pointer.Y) is { } grip)
            {
                Cursor = new Cursor(_CursorFor(grip));
                return;
            }

            if (selection.Selection is { } region && region.Contains(selection.ToImagePixel(pointer.X, pointer.Y)))
            {
                Cursor = new Cursor(StandardCursorType.SizeAll);
                return;
            }
        }

        Cursor = new Cursor(StandardCursorType.Cross);
    }

    // The resize cursor that says which way a grip moves. The two diagonal corners share their axis with the corner opposite them, the same way dragging one tips the rectangle onto the other's side.
    private static StandardCursorType _CursorFor(SelectionGrip grip) => grip switch
    {
        SelectionGrip.TopLeft or SelectionGrip.BottomRight => StandardCursorType.TopLeftCorner,
        SelectionGrip.TopRight or SelectionGrip.BottomLeft => StandardCursorType.TopRightCorner,
        SelectionGrip.Top or SelectionGrip.Bottom => StandardCursorType.SizeNorthSouth,
        _ => StandardCursorType.SizeWestEast,
    };

    // The panel a press landed on, or nothing where it landed on the picture. Self and ancestors, because the
    // padding between the tools has no child control of its own and a press there resolves to the panel.
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
                // Its own return rather than falling into the switch's shared tail below: that tail closes the
                // window the moment IsClosed turns true, which Confirm() does synchronously — before a preview
                // gate has had any chance to be asked. Going through _Confirm keeps the close behind the gate.
                e.Handled = true;
                _Confirm(selection);
                return;
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

    // The two keys that mean something while a note is open — everything else is marked handled,
    // stopping the shortcut it would be. Escape closes the note; Enter does the same, not confirm.
    private void _WhileTyping(ScreenshotSelectionViewModel selection, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Enter:
                selection.FinishTyping();

                // Handled, because these two are the surface's and produce no character worth having.
                e.Handled = true;
                break;
        }

        // Everything else deliberately left unhandled: Windows only turns a key into a character
        // when unhandled, so marking them all silently cut off the typing this exists to protect.
        _Draw();
    }

    // Points the keyboard at the hidden text box while a note is open, and takes it back afterwards. Everything
    // the operator types goes there — which is also what keeps the surface's shortcuts out of it, since the keys
    // are consumed before they are anything else.
    private void _FollowTyping(ScreenshotSelectionViewModel selection)
    {
        if (selection.Typing && !TypingTarget.IsFocused)
        {
            TypingTarget.Text = selection.Typed;
            TypingTarget.CaretIndex = TypingTarget.Text?.Length ?? 0;
            TypingTarget.Focus();
        }
        else if (!selection.Typing && TypingTarget.IsFocused)
        {
            TypingTarget.Text = string.Empty;
            Focus();
        }
    }

    // What is in that box is what the note says. Read whole rather than accumulated, because the box owns the editing.
    private void _OnTypedTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_selection is not { Typing: true } selection)
        {
            return;
        }

        selection.SetTyped(TypingTarget.Text ?? string.Empty);
        _Draw();
    }

    // Gives up when the surface loses desktop attention — one left behind covers every screen and
    // takes every key. Only after it was activated once, else a platform ordering quirk (not a real
    // alt-tab) would close the surface the instant it opened.
    private void _OnDeactivated(object? sender, EventArgs e)
    {
        if (!_wasActivated || _selection is not { IsClosed: false } selection)
        {
            return;
        }

        selection.Cancel();
        Close();
    }

    // The tools, chosen with the mouse (AC-358). Each makes exactly the call its key makes and is followed by
    // the same redraw — a button that did something subtly different from the key beside it would be worse than
    // no button at all.
    private void _OnRegionTool(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseRegion());

    private void _OnWindowTool(object? sender, RoutedEventArgs e) =>
        _Tool(selection => selection.PickWindows(!selection.PickingWindow));

    private void _OnEverythingTool(object? sender, RoutedEventArgs e) => _Tool(_ChooseEverything);

    private void _OnOutlineTool(object? sender, RoutedEventArgs e) =>
        _Tool(selection => selection.Outline(!selection.Outlining));

    // The inks and the line weights (AC-375). Each is the same shape of call as a tool, and each redraws — the
    // mark being dragged right now is previewed in what has just been chosen.
    private void _OnInkAccent(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseInk(_AccentColour()));

    private void _OnInkRed(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseInk(MarkInk.Red));

    private void _OnInkYellow(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseInk(MarkInk.Yellow));

    private void _OnInkGreen(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseInk(MarkInk.Green));

    private void _OnInkWhite(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseInk(MarkInk.White));

    private void _OnWeightThin(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseWeight(MarkWeight.Thin));

    private void _OnWeightMedium(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseWeight(MarkWeight.Medium));

    private void _OnWeightThick(object? sender, RoutedEventArgs e) => _Tool(selection => selection.ChooseWeight(MarkWeight.Thick));

    // Paints the swatches in the inks they stand for, and marks the chosen ink and weight. Restated on every draw
    // rather than bound, because which one is on is one value against eight controls — eight bindings, each of
    // which would have to be told what the other seven mean.
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

    // The whole capture in one press. Named here rather than written out twice so the button and the key it
    // carries cannot drift apart — the one thing this panel promises is that the two are the same surface said
    // twice. Window mode comes off first: what it marks out is a window, and taking everything is not that.
    private static void _ChooseEverything(ScreenshotSelectionViewModel selection)
    {
        selection.PickWindows(false);
        selection.SelectEverything();
    }

    // Puts both panels at the top of the display under the pointer, not the window's own middle.
    // Follows the pointer until moved by hand, then stops — unlike AC-358's auto-step-aside that rocked back and forth.
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

    // Puts a panel at a place on the surface, clamped so that all of it stays reachable. Clamped against the
    // window rather than the display: a screen narrower or shorter than the panel would otherwise push it off
    // the edge, and a panel half outside the window is a tool you cannot press.
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
        _FollowTyping(selection);

        var width = Surface.Bounds.Width;
        var height = Surface.Bounds.Height;
        if (selection.Selection is not { } region)
        {
            _Place(Marquee, 0, 0, 0, 0);
            _Place(ShadeTop, 0, 0, width, height);
            _Place(ShadeBottom, 0, 0, 0, 0);
            _Place(ShadeLeft, 0, 0, 0, 0);
            _Place(ShadeRight, 0, 0, 0, 0);
            _PlaceGrips(selection);
            Readout.IsVisible = false;
            return;
        }

        var (x, y, w, h) = selection.ToSurface(region);
        _Place(Marquee, x, y, w, h);
        _Place(ShadeTop, 0, 0, width, y);
        _Place(ShadeBottom, 0, y + h, width, Math.Max(0, height - (y + h)));
        _Place(ShadeLeft, 0, y, x, h);
        _Place(ShadeRight, x + w, y, Math.Max(0, width - (x + w)), h);
        _PlaceGrips(selection);

        // The size is reported in the image's pixels, which is what the session receives — the window's units
        // would be a different number on a scaled display and would read as a lie next to the attachment.
        _DrawMarks(selection);
        ReadoutText.Text = $"{region.Width} × {region.Height}";
        Readout.IsVisible = true;
        Canvas.SetLeft(Readout, x);
        Canvas.SetTop(Readout, Math.Max(0, y - 24));
    }

    // Shows what has been marked before the operator commits, including the one still being
    // dragged. A redaction is drawn solid, not bordered, since the operator is checking nothing
    // readable is left. Drawn from the same list, same order, that gets burnt in.
    private void _DrawMarks(ScreenshotSelectionViewModel selection)
    {
        var beingMade = selection.PendingMarkPreview;
        var drawn = beingMade is not null ? selection.Marks.Append(beingMade).ToList() : selection.Marks;

        for (var index = 0; index < drawn.Count; index++)
        {
            // The one still being made is always last — it is appended to the placed ones just above.
            _Show(
                _ShapeAt(index, drawn[index]), drawn[index], selection,
                pending: beingMade is not null && index == drawn.Count - 1);
        }

        for (var index = drawn.Count; index < _shapes.Count; index++)
        {
            // Kept, not removed: undo is often followed by another mark. Emptied, not merely
            // resized — a path draws its held geometry regardless of size, so shrinking alone wouldn't hide an arrow.
            _Empty(_shapes[index]);
        }
    }

    // The shape standing in for the mark at that position, made afresh where the kind there has changed. A frame
    // is a rectangle and an arrow is a path, and no amount of restyling turns one into the other — where an undo
    // leaves a different kind of mark at an index, the shape has to be replaced rather than repainted.
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

    // What a mark of that kind has to be drawn with. A frame is a rectangle, an arrow is a path, and a wash is an image because that is the only thing in Avalonia that carries a blend mode.
    private static Control _StandInFor(Mark mark) => mark switch
    {
        ArrowMark => new Path(),
        HighlightMark => new Image { Stretch = Stretch.Fill },
        StrokeMark => new Path(),
        // A plate with letters on it — the letters need one known background. The bar beside them is
        // the caret, which belongs only to the preview and must never end up in the picture.
        TextMark => new Border
        {
            Child = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Children = { new TextBlock(), new Rectangle() },
            },
        },
        _ => new Rectangle(),
    };

    // Whether a kept control is still the right thing for the mark now at its position.
    private static bool _Suits(Control shape, Mark mark) => (shape, mark) switch
    {
        (Path, ArrowMark or StrokeMark) => true,
        (Image, HighlightMark) => true,
        (Border, TextMark) => true,
        (Rectangle, RedactionMark or OutlineMark) => true,
        _ => false,
    };

    // Makes one shape look like the mark it stands for, restated every draw since shapes are reused
    // as marks come and go. `pending`: whether this mark is still being made — only the caret cares.
    private void _Show(Control shape, Mark mark, ScreenshotSelectionViewModel selection, bool pending)
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

                var written = (TextBlock)((StackPanel)plate.Child!).Children[0];
                var caret = (Rectangle)((StackPanel)plate.Child!).Children[1];
                var ink = new SolidColorBrush(Color.FromUInt32(note.Colour));
                var letters = selection.ToSurfaceLength(note.Size);

                written.Text = note.Text;
                written.FontSize = letters;
                written.Foreground = ink;

                // Only while this note is the one being typed into. An empty plate says nothing about whether the
                // surface is listening; a caret says it in the one way everyone already reads.
                caret.IsVisible = pending && selection.Typing;
                caret.Fill = ink;
                caret.Width = Math.Max(1, letters / 12);
                caret.Height = letters;
                caret.Margin = new Thickness(letters / 8, 0, 0, 0);
                caret.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

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

    // Lays the arrow's outline into a path in window units, from the same corner list the imaging
    // library fills — one shape converted twice, not two worked out twice. Geometry is written
    // relative to the shape's top-left, not the surface origin, else the empty space in front counts toward its size.
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

    // Lays the freehand line into its stand-in path, in window units, from the same curve the
    // imaging library draws. Was a pair of paths until AC-375 (a ring underneath, since a line
    // can't be drawn and ringed at once) — the ring left with the palette, so did the second path.
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

    // Puts the eight grips where GripPositions says, or hides all where there's nothing to grab.
    // AC-565 criterion 9: a mark tool in hand still leaves a non-empty selection, but a grip there
    // would promise a drag the surface won't honour.
    private void _PlaceGrips(ScreenshotSelectionViewModel selection)
    {
        var positions = selection.DraggingRegion ? selection.GripPositions() : [];
        foreach (var grip in Enum.GetValues<SelectionGrip>())
        {
            _GripControl(grip).IsVisible = false;
        }

        foreach (var (grip, x, y) in positions)
        {
            var control = _GripControl(grip);
            control.IsVisible = true;
            Canvas.SetLeft(control, x - (control.Width / 2));
            Canvas.SetTop(control, y - (control.Height / 2));
        }
    }

    // The shape standing in for a grip. Named lookup rather than a dictionary built every draw: the names are fixed by the XAML, and this is the one place that has to agree with it.
    private Rectangle _GripControl(SelectionGrip grip) => grip switch
    {
        SelectionGrip.TopLeft => GripTopLeft,
        SelectionGrip.Top => GripTop,
        SelectionGrip.TopRight => GripTopRight,
        SelectionGrip.Right => GripRight,
        SelectionGrip.BottomRight => GripBottomRight,
        SelectionGrip.Bottom => GripBottom,
        SelectionGrip.BottomLeft => GripBottomLeft,
        SelectionGrip.Left => GripLeft,
        _ => throw new NotSupportedException($"There is no grip control for {grip}."),
    };

    private static void _Place(Control shape, (double X, double Y, double Width, double Height) area) =>
        _Place(shape, area.X, area.Y, area.Width, area.Height);

    private static void _Place(Control shape, double x, double y, double width, double height)
    {
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        shape.Width = Math.Max(0, width);
        shape.Height = Math.Max(0, height);
    }

    // One pixel of a colour, to be stretched over a band. A real bitmap rather than a drawing of a filled
    // rectangle: the blend mode belongs to the image, and a drawing is composited before it ever gets there.
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

    // Leaves a kept shape drawing nothing — its size taken away, and, where it holds one, its geometry too.
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
