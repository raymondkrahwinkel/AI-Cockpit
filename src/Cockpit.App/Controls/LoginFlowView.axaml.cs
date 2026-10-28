using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

public partial class LoginFlowView : UserControl
{
    public LoginFlowView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void _OnCopyCodeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LoginFlowRowViewModel { HasDeviceCode: true } login
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(login.DeviceCode);
        }
    }
}
