using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Cockpit.App.Views;

/// <summary>
/// Options dialog (#13): a categorised replacement for the sidebar's Options flyout, which had grown
/// too tall for a popup. Its <see cref="Window.DataContext"/> is the shared <c>CockpitViewModel</c>
/// passed in by <see cref="Cockpit.App.Services.SessionDialogService.ShowOptionsDialogAsync"/> — every
/// binding here is unchanged from the flyout, just re-hosted under tabs.
/// </summary>
public partial class OptionsDialog : Window
{
    public OptionsDialog()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
