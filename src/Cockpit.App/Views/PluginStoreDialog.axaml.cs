using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// #62: plugin store dialog. Disposes the view model on close so it unsubscribes from the
// shared long-lived PluginManagerViewModel's events — else every open leaks a subscription.
public partial class PluginStoreDialog : Window
{
    public PluginStoreDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        (DataContext as PluginStoreDialogViewModel)?.Dispose();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Owned modal on the same shared manager, so add/remove there refreshes this dialog too.
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
