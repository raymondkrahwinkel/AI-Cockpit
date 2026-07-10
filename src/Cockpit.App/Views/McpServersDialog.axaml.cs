using Avalonia.Controls;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

/// <summary>
/// Modal dialog for editing the shared MCP-server registry (#26). Closing is wired by
/// <see cref="Services.SessionDialogService"/> (it subscribes the view model's CloseRequested to
/// <see cref="Window.Close()"/>); here we only apply the shared custom window chrome so it matches the
/// other dialogs instead of showing the default OS title bar.
/// </summary>
public partial class McpServersDialog : Window
{
    public McpServersDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }
}
