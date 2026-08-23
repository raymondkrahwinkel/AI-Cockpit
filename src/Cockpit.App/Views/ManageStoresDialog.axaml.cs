using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

// #62: manage-plugin-stores dialog, bound straight to the shared PluginManagerViewModel so
// add/remove refreshes the store dialog behind it from the same instance. Opened as an owned
// modal from that dialog's "Manage stores" button.
public partial class ManageStoresDialog : Window
{
    public ManageStoresDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
