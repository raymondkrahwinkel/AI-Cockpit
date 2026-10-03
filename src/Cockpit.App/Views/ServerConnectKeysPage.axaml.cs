using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class ServerConnectKeysPage : UserControl
{
    public ServerConnectKeysPage()
    {
        InitializeComponent();
    }

    // The clipboard is the view's: the key goes there from the view model's one copy and nowhere else.
    private async void OnCopyIssuedKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ServerAdminViewModel { IssuedSecret: { } secret } admin && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try
            {
                await clipboard.SetTextAsync(secret);
            }
            catch (Exception exception)
            {
                admin.Status = $"Could not copy the key: {exception.Message}";
            }
        }
    }
}
