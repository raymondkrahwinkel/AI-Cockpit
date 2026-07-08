using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _mainWindow;

    /// <summary>
    /// True once a real quit was requested (tray "Quit"), so <see cref="MainWindow"/> lets the close
    /// through instead of hiding to tray (#33). Distinguishes a genuine quit from a close-to-tray.
    /// </summary>
    public bool IsQuitting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _mainWindow = new MainWindow
            {
                DataContext = Program.Services.GetRequiredService<CockpitViewModel>(),
            };
            desktop.MainWindow = _mainWindow;
            _SetUpTrayIcon();

            // Fire-and-forget (#34): a no-op when voice or global push-to-talk is off, so the
            // portal/keyboard-hook is only ever touched for an operator who opted in.
            _ = Program.Services.GetRequiredService<VoicePushToTalkCoordinator>().StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Restores and focuses the main window (tray left-click / "Show cockpit").</summary>
    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>Really quits the app (tray "Quit") — lets MainWindow's close through, then the normal teardown runs.</summary>
    public void RequestQuit()
    {
        IsQuitting = true;
        _desktop?.Shutdown();
    }

    // A tray icon is always present while the app runs so the operator can immediately see whether the
    // tray works on their desktop (on GNOME/Wayland a legacy tray may need an AppIndicator extension).
    // Only when the "minimize to tray on close" setting is on does closing hide to it (#33) — otherwise
    // the tray is just a quick Show/Quit affordance and closing quits as usual.
    private void _SetUpTrayIcon()
    {
        var showItem = new NativeMenuItem("Show cockpit");
        showItem.Click += (_, _) => ShowMainWindow();
        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => RequestQuit();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Cockpit.App/Assets/avalonia-logo.ico"))),
            ToolTipText = "Cockpit",
            Menu = new NativeMenu { Items = { showItem, quitItem } },
        };
        tray.Clicked += (_, _) => ShowMainWindow();

        TrayIcon.SetIcons(this, [tray]);
    }
}
