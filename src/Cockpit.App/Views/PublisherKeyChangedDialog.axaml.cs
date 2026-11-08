using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

public partial class PublisherKeyChangedDialog : Window
{
    public PublisherKeyChangedDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        CockpitWindowChrome.EnableConfirmationKeys(this);
    }

    private void OnKeep(object? sender, RoutedEventArgs e) => Close(false);

    private void OnTrust(object? sender, RoutedEventArgs e) => Close(true);
}
