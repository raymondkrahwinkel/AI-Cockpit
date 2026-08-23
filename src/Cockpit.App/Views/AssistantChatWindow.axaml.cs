using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Views;

// AC-543 (criteria 7-9, 11): pop-out chat window, a peephole onto the assistant's own standing
// session, never its owner. Since AC-952 the chat surface is AssistantChatView and this is only
// the window around it — resize grip, drag-to-dock, saved bounds, minimised-renderer pause.
public partial class AssistantChatWindow : Window
{
    // AC-866: this window's own key in the (now keyed, AC-866) window-bounds store — kept apart from the main
    // window's "main" so the two never collide.
    private const string BoundsKey = "assistant";

    // AC-962: the drop zone is this share of the working area of the screen the cockpit stands on, measured in
    // from its right edge — the band where the dock rail belongs. The ticket fixes it inside 10–25%.
    private const double DropZoneScreenFraction = 0.20;

    private readonly IWindowBoundsStore? _windowBoundsStore;

    // AC-962, the managed move: where the window stood when the drag started so Esc can put it back, and where in
    // the window the pointer took hold so it keeps following that same point. Null start means no drag is running.
    private PixelPoint? _dragStartPosition;
    private PixelPoint _dragGrabOffset;
    private IPointer? _dragPointer;

    // The band a release docks in, in screen coordinates, and the cockpit that draws it. Resolved once per drag —
    // the cockpit cannot move to another screen while the pointer is held down here.
    private PixelRect? _dropZone;
    private CockpitViewModel? _dropZoneOwner;

    // The cockpit window the drop zone is measured against, handed over by `AssistantIndicatorCoordinator` — the
    // one place that already looks the lifetime's main window up.
    internal Window? CockpitWindow { get; set; }

    // The last normal (non-maximized) position/size — mirrors MainWindow's own fields, same reason: Avalonia
    // reports the maximized size while maximized, so this is what a maximized window saves as "restore to".
    private PixelPoint _normalPosition;
    private Size _normalSize;

    // Cockpit serves no external UI-Automation tree (see NoChildrenWindowPeer) — the assistant has its own in-app
    // voice channel, and exposing one to external UIA clients leaks the transcript (Avalonia #8240). The window
    // still returns a real root peer; only its children are hidden.
    protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new NoChildrenWindowPeer(this);

    public AssistantChatWindow()
        : this(Program.Services?.GetService<IWindowBoundsStore>())
    {
    }

    // Test seam: lets a test control what the bounds-restore below reads, same shape as MainWindow's own.
    internal AssistantChatWindow(IWindowBoundsStore? windowBoundsStore)
    {
        _windowBoundsStore = windowBoundsStore;
        InitializeComponent();
        WindowResizeGrip.Apply(this);

        // AC-962: AC-934's WindowDecorationProperties.SetElementRole block is deliberately gone. It made the header
        // the native caption, so Windows ran the move itself and _OnHeaderPressed never fired — the drag-to-dock
        // gesture cannot exist beside it. Aero Snap on this window is the accepted price.

        // No OS title bar (WindowResizeGrip.Apply, AC-636/AC-678), so the view's header is this window's drag
        // handle — wired from here rather than from the view, which has no window to move when it is docked.
        ChatView.HeaderBar.PointerPressed += _OnHeaderPressed;
        ChatView.HeaderBar.PointerMoved += _OnHeaderMoved;
        ChatView.HeaderBar.PointerReleased += _OnHeaderReleased;
        ChatView.HeaderBar.PointerCaptureLost += _OnHeaderCaptureLost;
        AddHandler(KeyDownEvent, _OnKeyDownTunnel, RoutingStrategies.Tunnel);

        _normalPosition = Position;
        _normalSize = new Size(Width, Height);

        // AC-866: restore before Show() (AC-801's X11-WM race) and force Manual, since CenterOwner has no owner
        // to anchor to here (shown ownerless — AssistantIndicatorCoordinator._OpenChatAsync). Position restore
        // assumes XWayland/X11 (Avalonia 12.1); a native Wayland backend would silently stop restoring it.
        var saved = _windowBoundsStore?.LoadAsync(BoundsKey).GetAwaiter().GetResult();
        if (saved is { HasUsableSize: true } && RestoredWindowBounds.IsOnAScreen(saved, Screens.All.Select(s => s.WorkingArea)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(saved.X, saved.Y);
            Width = saved.Width;
            Height = saved.Height;
            _normalPosition = Position;
            _normalSize = new Size(saved.Width, saved.Height);

            if (saved.IsMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
    }

    // AC-866: mirrors MainWindow's own OnResized — tracked separately from WindowState so a maximized window
    // still saves the bounds to restore to when un-maximized (Avalonia reports the maximized size while maximized).
    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        if (WindowState == WindowState.Normal)
        {
            _normalPosition = Position;
            _normalSize = new Size(Width, Height);
        }
    }

    // AC-866: fire-and-forget, unlike MainWindow's deferred close — this window closing never ends the app (it is
    // a peephole, see the class remarks), so there is nothing waiting on this write to finish.
    private async Task _SaveBoundsAsync()
    {
        if (_windowBoundsStore is null)
        {
            return;
        }

        var bounds = new WindowBounds(
            _normalPosition.X,
            _normalPosition.Y,
            (int)_normalSize.Width,
            (int)_normalSize.Height,
            WindowState == WindowState.Maximized);

        try
        {
            await _windowBoundsStore.SaveAsync(BoundsKey, bounds).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort: a failed save must not affect anything else — the window is already closing regardless.
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ChatView.SetHostMinimised(WindowState == WindowState.Minimized);
    }

    // Closing this window must never end the assistant's conversation (criterion 7) — disposing the
    // view model is the peephole being let go; AssistantChatViewModel.Dispose only detaches its
    // own subscription, never the session.
    protected override void OnClosed(EventArgs e)
    {
        _ = _SaveBoundsAsync();

        // AC-953: docking closes this window too, but there the view model is being handed to the rail rather
        // than let go — only a real close is the peephole ending.
        if (DataContext is AssistantChatViewModel { IsDocked: false } vm)
        {
            vm.Dispose();
        }

        base.OnClosed(e);
    }

    // AC-883: while minimised this window's renderer is paused, so recycled transcript rows never
    // get the compositor commit that removes their visuals. Guarded on ChatView because
    // WindowState initialises before InitializeComponent has built it.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty && ChatView is not null)
        {
            ChatView.SetHostMinimised(change.GetNewValue<WindowState>() == WindowState.Minimized);
        }
    }

    // The header is the drag handle. AC-962 replaced BeginMoveDrag with a move this window runs itself: the OS
    // move loop reports neither the pointer nor the release, so it offers no moment at which to hit-test a drop.
    private void _OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button or ToggleButton)
        {
            return;
        }

        _dragStartPosition = Position;
        _dragGrabOffset = this.PointToScreen(e.GetPosition(this)) - Position;
        _dragPointer = e.Pointer;
        e.Pointer.Capture(ChatView.HeaderBar);
        _ShowDropZone();
    }

    // Absolute rather than incremental: the window is put where the pointer is, less the grip it was taken by. A
    // per-move delta would drift, since moving the window moves the client coordinates the delta is measured in.
    private void _OnHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartPosition is null)
        {
            return;
        }

        var pointer = this.PointToScreen(e.GetPosition(this));
        Position = pointer - _dragGrabOffset;

        if (_dropZoneOwner is { } cockpit)
        {
            cockpit.IsAssistantDropZoneActive = _dropZone?.Contains(pointer) == true;
        }
    }

    private void _OnHeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStartPosition is null)
        {
            return;
        }

        var dropped = _dropZone?.Contains(this.PointToScreen(e.GetPosition(this))) == true;
        _EndDrag();

        // The same path the header's Dock button takes (AC-953's `_ShowInAsync`): this window only exists while
        // the assistant is undocked, so asking for the other host from here is asking to dock.
        if (dropped && DataContext is AssistantChatViewModel chat)
        {
            chat.ToggleDockCommand.Execute(null);
        }
    }

    // The pointer can also be taken away rather than let go — another window grabbing it, a cancelled touch, a
    // backend withdrawing the capture. No release follows, so without this the zone would stay on screen. The
    // window keeps the position it had reached: the drag was interrupted, not abandoned the way Esc abandons it.
    private void _OnHeaderCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _EndDrag();

    // Esc abandons the move — the window goes back where it was picked up and nothing docks (AC-962 criterion 6).
    // Tunnelled, so it is seen before the composer below can take an Escape of its own for closing a picker.
    private void _OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _dragStartPosition is not { } start)
        {
            return;
        }

        Position = start;
        _EndDrag();
        e.Handled = true;
    }

    // Where a release docks: the band along the right edge of the screen the cockpit stands on, drawn over the
    // part of it the cockpit window actually covers. No overlap means no zone and no drop — there is nothing to
    // show the operator, and docking into a rail they cannot see is a window vanishing for no stated reason.
    private void _ShowDropZone()
    {
        if (CockpitWindow is not { } cockpitWindow || cockpitWindow.DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        if ((cockpitWindow.Screens.ScreenFromWindow(cockpitWindow) ?? cockpitWindow.Screens.Primary)
            is not { WorkingArea: var area })
        {
            return;
        }

        var band = (int)(area.Width * DropZoneScreenFraction);
        var zone = new PixelRect(area.Right - band, area.Y, band, area.Height);
        var covered = zone.Intersect(new PixelRect(
            cockpitWindow.PointToScreen(default),
            PixelSize.FromSize(cockpitWindow.ClientSize, cockpitWindow.RenderScaling)));

        if (covered.Width <= 0)
        {
            return;
        }

        _dropZone = zone;
        _dropZoneOwner = cockpit;
        cockpit.AssistantDropZoneWidth = covered.Width / cockpitWindow.RenderScaling;
    }

    private void _EndDrag()
    {
        _dragStartPosition = null;
        _dropZone = null;
        _dragPointer?.Capture(null);
        _dragPointer = null;

        if (_dropZoneOwner is { } cockpit)
        {
            cockpit.AssistantDropZoneWidth = 0;
            cockpit.IsAssistantDropZoneActive = false;
            _dropZoneOwner = null;
        }

        // AC-866: a managed move changes the position without ever raising a resize, so the bounds this window
        // saves as "restore to" are brought up to date here instead.
        if (WindowState == WindowState.Normal)
        {
            _normalPosition = Position;
        }
    }
}
