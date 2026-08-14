using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Controls;
using Cockpit.App.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;

namespace Cockpit.App.Views;

public partial class MainWindow : Window
{
    private readonly IWindowBoundsStore? _windowBoundsStore = Program.Services?.GetService<IWindowBoundsStore>();

    // The last normal (non-maximized) position/size, tracked so a maximized window still saves the bounds to
    // restore to when un-maximized — Avalonia reports the maximized size while maximized.
    private PixelPoint _normalPosition;
    private Size _normalSize;

    // AC-779: OnClosing defers the real close until the bounds save completes (see below) rather than blocking
    // the UI thread on it; these track that in-flight save so a second close request while it is still running
    // doesn't start a duplicate one.
    private bool _boundsSaved;
    private Task? _saveBoundsThenCloseTask;

    public MainWindow()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, titleBar: CockpitTitleBar.Window, includeMinimize: true, includeMaximize: true, closeOnEscape: false);

        Activated += (_, _) => _SetWindowActive(true);
        Deactivated += (_, _) => _SetWindowActive(false);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        _normalPosition = Position;
        _normalSize = new Size(Width, Height);

        // Restore the last-used bounds (#: window bounds) so the app reopens where it was, instead of the
        // OS-chosen random spot/size. Off-screen or degenerate saved bounds fall back to the XAML default.
        var saved = _windowBoundsStore?.LoadAsync().GetAwaiter().GetResult();
        if (saved is { HasUsableSize: true } && _IsOnAScreen(saved))
        {
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

    // Whether this window is the active one is something only the window knows, and the finished-session
    // notification needs it: a session you are looking at has already told you it is done. Window activation,
    // not keyboard focus — a click in the terminal moves focus around inside a window that stayed active.
    private void _SetWindowActive(bool isActive)
    {
        if (DataContext is CockpitViewModel cockpit)
        {
            cockpit.IsWindowActive = isActive;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Close-to-tray (#33): when the setting is on and this is a real window close (not a quit
        // requested from the tray), cancel the close and hide to the tray instead — the app keeps
        // running. A tray "Quit" sets App.IsQuitting, so that path falls through to a normal close.
        if (App is { IsQuitting: false }
            && DataContext is CockpitViewModel { MinimizeToTrayOnClose: true })
        {
            LifecycleLog.Write("Main window close intercepted; hiding to the tray, the app keeps running.");
            e.Cancel = true;
            Hide();
            return;
        }

        // AC-779: the bounds save used to block this thread with GetAwaiter().GetResult(); a slow write (AV scan,
        // network profile) froze the close. Deferred one round trip instead — cancel this close, await the save,
        // then replay Close() so the real shutdown below runs once it's done.
        if (!_boundsSaved)
        {
            e.Cancel = true;
            _saveBoundsThenCloseTask ??= _SaveBoundsThenCloseAsync();
            return;
        }

        // The one that answers "why was the cockpit gone when I came back?": with no shutdown asked for, a close
        // arriving here is the last window going, which ends the app — and knowing whether one ever arrived is the
        // difference between something closing the window and the process being ended from outside.
        LifecycleLog.Write($"Main window closing for real (quit requested: {App?.IsQuitting}); the app will end with it.");
        base.OnClosing(e);
    }

    private async Task _SaveBoundsThenCloseAsync()
    {
        // A failed save must not leave the window refusing to close forever (e.Cancel above already stopped the
        // first attempt) — so this still counts as done and lets the close through either way.
        try
        {
            await _SaveBoundsAsync();
        }
        catch (Exception ex)
        {
            LifecycleLog.Write($"Saving window bounds on close failed, closing anyway: {ex.Message}");
        }

        _boundsSaved = true;
        Close();
    }

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);
        if (WindowState == WindowState.Normal)
        {
            _normalPosition = Position;
            _normalSize = new Size(Width, Height);
        }
    }

    private Task _SaveBoundsAsync()
    {
        if (_windowBoundsStore is null)
        {
            return Task.CompletedTask;
        }

        var bounds = new WindowBounds(
            _normalPosition.X,
            _normalPosition.Y,
            (int)_normalSize.Width,
            (int)_normalSize.Height,
            WindowState == WindowState.Maximized);

        // Awaited rather than blocked on (AC-779): WindowBoundsStore.SaveAsync is genuinely async I/O all the way
        // down (ConfigureAwait(false) throughout), so this never needs a Task.Run to avoid blocking the caller.
        return _windowBoundsStore.SaveAsync(bounds);
    }

    // True when the saved rectangle overlaps a currently-connected screen, so a window saved on a monitor that
    // is now unplugged doesn't reopen off in invisible space.
    private bool _IsOnAScreen(WindowBounds bounds)
    {
        foreach (var screen in Screens.All)
        {
            var area = screen.Bounds;
            var intersectsX = bounds.X < area.X + area.Width && bounds.X + bounds.Width > area.X;
            var intersectsY = bounds.Y < area.Y + area.Height && bounds.Y + bounds.Height > area.Y;
            if (intersectsX && intersectsY)
            {
                return true;
            }
        }

        return false;
    }

    private static App? App => Avalonia.Application.Current as App;
}
