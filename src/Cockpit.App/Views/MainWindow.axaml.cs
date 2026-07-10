using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, "Cockpit", includeMinimize: true, includeMaximize: true);
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

        base.OnClosing(e);
    }

    private static App? App => Avalonia.Application.Current as App;
}
