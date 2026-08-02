using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

// The Manage-plugin-stores dialog (#62): add or remove the stores the catalogue browses, each shown with
// the name, icon and plugin count its `index.json` advertises. Bound straight to the shared
// `ViewModels.PluginManagerViewModel` — add/remove go through its own commands, so the store
// dialog behind it refreshes its catalogue and sidebar from the same instance. Opened as an owned modal
// from the store dialog's "Manage stores" button.
public partial class ManageStoresDialog : Window
{
    public ManageStoresDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
