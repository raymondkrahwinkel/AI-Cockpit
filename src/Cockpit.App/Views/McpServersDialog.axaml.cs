using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// The shared MCP-server registry, edited in a window beside the cockpit (#26). Closing is wired by
// `Services.SessionDialogService` (it subscribes the view model's CloseRequested to
// `Window.Close()`); here we only apply the shared custom window chrome so it matches the
// other dialogs instead of showing the default OS title bar.
public partial class McpServersDialog : Window
{
    public McpServersDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    // The OS close button and Escape close the window without going through the Cancel or Save commands, so they
    // are the one route the view model cannot see on its own — tell it here (AC-499 review fix, finding 6), so an
    // interactive sign-in in flight is cancelled rather than left running against a discarded view model.
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as McpServersViewModel)?.OnWindowClosed();
    }
}
