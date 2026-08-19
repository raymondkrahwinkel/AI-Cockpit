using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Views;

// The pop-out chat window (AC-543 criteria 7–9, 11): a peephole onto the assistant's own standing session,
// never its owner. Since AC-952 the chat surface itself is `AssistantChatView` and this is the window around
// it — everything here is window-shaped and only window-shaped: the resize grip, the Windows caption roles,
// drag-to-move, the saved bounds, and the minimised-renderer pause.
public partial class AssistantChatWindow : Window
{
    // AC-866: this window's own key in the (now keyed, AC-866) window-bounds store — kept apart from the main
    // window's "main" so the two never collide.
    private const string BoundsKey = "assistant";

    private readonly IWindowBoundsStore? _windowBoundsStore;

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

        if (OperatingSystem.IsWindows())
        {
            // AC-934: marks the header as the native caption so dragging it triggers Aero Snap; the buttons
            // inside opt back out to User, or Windows would swallow their clicks as a caption drag instead.
            WindowDecorationProperties.SetElementRole(ChatView.HeaderBar, WindowDecorationsElementRole.TitleBar);
            WindowDecorationProperties.SetElementRole(ChatView.ListeningModeToggle, WindowDecorationsElementRole.User);
            WindowDecorationProperties.SetElementRole(ChatView.ReadAloudToggle, WindowDecorationsElementRole.User);
            WindowDecorationProperties.SetElementRole(ChatView.HistoryButton, WindowDecorationsElementRole.User);
            WindowDecorationProperties.SetElementRole(ChatView.DockToggleButton, WindowDecorationsElementRole.User);
            WindowDecorationProperties.SetElementRole(ChatView.CloseButton, WindowDecorationsElementRole.User);
        }

        // No OS title bar (WindowResizeGrip.Apply, AC-636/AC-678), so the view's header is this window's drag
        // handle — wired from here rather than from the view, which has no window to move when it is docked.
        ChatView.HeaderBar.PointerPressed += _OnHeaderPressed;

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

    // Closing this window must never end the assistant's conversation (criterion 7) — the view's own teardown
    // runs on detach and leaves the session alone. Disposing the view model is the peephole being let go, which
    // is this window and not the view: AssistantChatViewModel.Dispose only detaches its own subscription, never
    // the session — see its own remarks.
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

    // AC-883, the half of the pause only a window has: while this window is minimised its renderer is paused, so
    // the transcript's recycled rows never get the compositor commit that removes their scene visuals and pile up.
    // The view does the collapsing; this only says when. Guarded on ChatView because WindowState initialises
    // before InitializeComponent has built it.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty && ChatView is not null)
        {
            ChatView.SetHostMinimised(change.GetNewValue<WindowState>() == WindowState.Minimized);
        }
    }

    // The header is the drag handle — same idiom CockpitWindowChrome uses elsewhere, just not reused since that
    // helper's bar has no room for the read-aloud toggle. WindowResizeGrip covers the edges/corners it does not.
    private void _OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Button and not ToggleButton)
        {
            BeginMoveDrag(e);
        }
    }
}
