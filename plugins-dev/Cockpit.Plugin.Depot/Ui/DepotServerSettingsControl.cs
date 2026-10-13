using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

// AC-1511: Depot on a server, from Admin → Plugins. With no browser there, the connection signs in with an installation
// token, which the channel takes to the backend part on that server; Connect clears it from the box and nothing keeps it.
internal sealed class DepotServerSettingsControl : UserControl
{
    private readonly IPluginUiChannel _channel;
    private readonly TextBox _name = new() { PlaceholderText = "Name (e.g. Work)" };
    private readonly TextBox _url = new() { PlaceholderText = "https://depot.example.com" };
    private readonly TextBox _token = new() { PasswordChar = '•', PlaceholderText = "Installation token" };
    private readonly Button _connect = new() { Content = "Connect" };
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };

    public DepotServerSettingsControl(IPluginUiChannel channel)
    {
        _channel = channel;
        _connect.Click += async (_, _) => await ConnectAsync().ConfigureAwait(true);

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 6,
            Children =
            {
                _Hint("Connects this server to a Depot instance. The server has no browser to sign in with, so it uses an installation token: make one in Depot under Account. It works once and is not stored."),
                _Hint("Name"),
                _name,
                _Hint("Instance URL"),
                _url,
                _Hint("Installation token"),
                _token,
                _connect,
                _status,
            },
        };
    }

    public async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_url.Text) || string.IsNullOrWhiteSpace(_token.Text))
        {
            _status.Text = "Enter a name, the Depot address and an installation token.";
            return;
        }

        _connect.IsEnabled = false;
        _status.Text = "Connecting…";
        var token = _token.Text.Trim();
        _token.Text = string.Empty;
        try
        {
            var answer = await _channel.InvokeAsync(
                DepotChannel.ConnectWithInstallToken,
                JsonSerializer.SerializeToElement(
                    new DepotInstallTokenRequest(new DepotConnectionPayload(string.Empty, _name.Text.Trim(), _url.Text.Trim()), token),
                    DepotChannel.Json)).ConfigureAwait(true);
            _status.Text = answer.Deserialize<DepotInstallTokenAnswer>(DepotChannel.Json)?.Message
                ?? "The server gave no answer. Try again.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not the exception's text: what a failed call says is the server's business, not this view's.
            _status.Text = "The server could not be asked. Check the connection to it and try again.";
        }
        finally
        {
            _connect.IsEnabled = true;
        }
    }

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
