using System;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Controls;
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

    public MainWindow()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, "Cockpit", includeMinimize: true, includeMaximize: true);
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Close-to-tray (#33): when the setting is on and this is a real window close (not a quit
        // requested from the tray), cancel the close and hide to the tray instead — the app keeps
        // running. A tray "Quit" sets App.IsQuitting, so that path falls through to a normal close.
        if (App is { IsQuitting: false }
            && DataContext is CockpitViewModel { MinimizeToTrayOnClose: true })
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _SaveBounds();
        base.OnClosing(e);
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

    private void _SaveBounds()
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

        // Synchronous on shutdown: a small config write we want completed before the process exits.
        _windowBoundsStore.SaveAsync(bounds).GetAwaiter().GetResult();
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
