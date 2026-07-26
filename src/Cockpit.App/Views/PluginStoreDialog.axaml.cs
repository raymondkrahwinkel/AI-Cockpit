using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// The plugin store dialog (#62): sidebar/search/sort/grid/detail over a
/// <see cref="PluginStoreDialogViewModel"/>. Disposes the view model on close so it unsubscribes from
/// the shared (long-lived) <see cref="PluginManagerViewModel"/>'s collection/property-changed events —
/// otherwise every store-dialog open would leak one more subscription on that shared instance.
/// </summary>
public partial class PluginStoreDialog : Window
{
    public PluginStoreDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        DialogScreenClamp.Apply(this);
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        (DataContext as PluginStoreDialogViewModel)?.Dispose();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Opens the Manage-stores dialog as an owned modal over this one, on the same shared manager — so adding
    // or removing a store there refreshes this dialog's catalogue and sidebar from the one instance.
    private async void OnManageStores(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PluginStoreDialogViewModel viewModel)
        {
            await new ManageStoresDialog { DataContext = viewModel.Manager }.ShowDialog(this);
        }
    }

    private void OnOpenHomepage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PluginStoreDialogViewModel { SelectedPlugin.Homepage: { } url })
        {
            ExternalLink.TryOpen(url);
        }
    }

    private void OnOpenRepository(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PluginStoreDialogViewModel { SelectedPlugin.Repository: { } url })
        {
            ExternalLink.TryOpen(url);
        }
    }

}
