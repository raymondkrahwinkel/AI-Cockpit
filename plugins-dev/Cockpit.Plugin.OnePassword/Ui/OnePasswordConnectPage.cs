using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Vault;

namespace Cockpit.Plugin.OnePassword.UI;

// Mockup 4 (AC-2333): test a service-account token, see which vaults it reads, connect only when that is exactly one.
// The token is sent to the backend once, on Test or Connect, and is never kept here after that.
internal sealed class OnePasswordConnectPage : UserControl
{
    private const string InstallPage = "https://developer.1password.com/docs/cli/get-started/";

    private readonly IPluginUiChannel _channel;
    private readonly ICockpitUiHost _host;
    private readonly ContentControl _cliPanel = new();
    private readonly TextBlock _cliStatus = _Text(string.Empty, 12);
    private readonly Button _retryCli = new() { Content = "Retry CLI installation", IsVisible = false };
    private readonly Button _installFallback = new() { Content = "Open the 1Password CLI install page", IsVisible = false };
    private readonly ChannelVaultContext _context;
    private readonly TextBox _token = new() { PasswordChar = '•', PlaceholderText = "ops_…", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _test = new() { Content = "Test connection" };
    private readonly Button _connect = new() { IsVisible = false };
    private readonly Button _disconnect = new() { Content = "Disconnect", IsVisible = false };
    private readonly StackPanel _result = new() { Spacing = 6 };
    private readonly TextBlock _connected = _Text(string.Empty, 12.5);
    private TestReply? _tested;

    public OnePasswordConnectPage(ICockpitUiHost host)
    {
        _host = host;
        var channel = host.Channel;
        _channel = channel;
        _context = new ChannelVaultContext(channel);
        var tokenRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        tokenRow.Children.Add(_token);
        Grid.SetColumn(_test, 1);
        tokenRow.Children.Add(_test);
        _connect.Classes.Add("Accent");
        _test.Click += async (_, _) => await _TestAsync();
        _retryCli.Click += async (_, _) => await _PrepareCliAsync();
        _installFallback.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(new Uri(InstallPage));
            }
        };
        _connect.Click += async (_, _) => await _ConnectAsync();
        _disconnect.Click += async (_, _) =>
        {
            await _CallAsync<bool>(OnePasswordChannel.Disconnect, null);
            await _RefreshAsync();
        };
        _token.TextChanged += (_, _) =>
        {
            _tested = null;
            _connect.IsVisible = false;
        };
        _context.AvailabilityChanged += (_, _) => Dispatcher.UIThread.Post(_ApplyAvailability);

        // No ScrollViewer: the host dialog already wraps every settings view in one.
        Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new VaultEncryptionRequiredPanel(_context),
                _cliStatus,
                _cliPanel,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _retryCli, _installFallback } },
                _Text("Lets your agents use items from one 1Password vault through Cockpit's vault. Agents get names; the value goes only into a process when it starts.", 12.5),
                _Text("Use a separate vault for agents. Create a vault that holds only what agents may use, for example Agents, and copy those items into it. Then create a service account in 1Password that can read only that vault. Never give it your private or shared vaults: whatever the token can read, an approved agent process can receive.", 11.5),
                _Heading("1. SERVICE ACCOUNT"),
                tokenRow,
                _Text("Created in 1Password under Developer › Service accounts. Stored encrypted with your other credentials (Options › Security); agents and the vault MCP can never read it. Needs the 1Password CLI (op) 2.18.0 or later.", 11),
                _Heading("2. WHICH VAULT IT READS"),
                _connected,
                _result,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _connect, _disconnect } },
            },
        };
        _ = _InitializeAsync();
    }

    private async Task _InitializeAsync()
    {
        await _PrepareCliAsync();
        await _RefreshAsync();
    }

    private async Task<bool> _PrepareCliAsync()
    {
        _test.IsEnabled = false;
        _retryCli.IsVisible = false;
        _installFallback.IsVisible = false;
        _cliStatus.Text = "Installing 1Password CLI and verifying its signature…";
        try
        {
            var response = await _channel.InvokeAsync(OnePasswordChannel.PrepareCli, JsonSerializer.SerializeToElement(true));
            var result = response.Deserialize<ManagedCliInstallResult>(OnePasswordChannel.Json);
            if (result is not { Success: true })
            {
                throw new InvalidOperationException(result?.Error ?? "The plugin returned no CLI installation result.");
            }

            _cliStatus.Text = result.ExecutablePath == "op" ? "Using system 1Password CLI (2.18.0 or later)." : string.Empty;
            _cliPanel.Content ??= new ManagedCliConfigSection(_host, "op", "1Password CLI").View;
            _ApplyAvailability();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            _cliStatus.Text = $"Could not install 1Password CLI: {exception.Message}";
            _retryCli.IsVisible = true;
            _installFallback.IsVisible = true;
            return false;
        }
    }

    private async Task _RefreshAsync()
    {
        if (await _CallAsync<StatusReply>(OnePasswordChannel.Status, null) is not { } status)
        {
            return;
        }

        _context.Availability = Enum.TryParse<VaultAvailability>(status.Availability, out var availability) ? availability : VaultAvailability.EncryptionOff;
        _ApplyAvailability();
        var isConnected = status.State == "Connected";
        _disconnect.IsVisible = isConnected;
        _connected.Text = status.State switch
        {
            "Connected" => $"Connected · service account · reads one vault: {status.Vault}",
            "Error" => $"Error: {status.Error}",
            _ => "Not connected. Test the connection to see which vaults a token can read; nothing is saved until you connect.",
        };
    }

    private void _ApplyAvailability()
    {
        var open = _context.Availability == VaultAvailability.Available;
        _token.IsEnabled = open;
        _test.IsEnabled = open;
        _connect.IsEnabled = open;
        _disconnect.IsEnabled = open;
    }

    private async Task _TestAsync()
    {
        if (!await _PrepareCliAsync())
        {
            return;
        }

        _test.IsEnabled = false;
        _result.Children.Clear();
        _tested = await _CallAsync<TestReply>(OnePasswordChannel.Test, new TokenRequest(_token.Text ?? string.Empty));
        _ApplyAvailability();
        if (_tested is not { } reply)
        {
            return;
        }

        if (!reply.Ok)
        {
            _result.Children.Add(_Notice($"Could not check the token: {reply.Error}.", isError: true));
            return;
        }

        foreach (var vault in reply.Vaults)
        {
            _result.Children.Add(_Text($"{vault.Name} · {vault.Items} items", 12.5));
        }

        if (reply.Vaults.Count == 1)
        {
            _result.Children.Add(_Notice($"The only vault. This token reads exactly one vault. Agents can ask for items in {reply.Vaults[0].Name}; each item still needs your consent per session or project.", isError: false));
            _connect.Content = $"Connect vault {reply.Vaults[0].Name}";
            _connect.IsVisible = true;
        }
        else
        {
            var count = reply.Vaults.Count == 0 ? "no vault" : $"{reply.Vaults.Count} vaults";
            _result.Children.Add(_Notice($"This token can read {count}. Connect a service account that reads one separate vault only. Limit it in 1Password, or create a new one, then test again.", isError: true));
        }
    }

    private async Task _ConnectAsync()
    {
        _connect.IsEnabled = false;
        var status = await _CallAsync<StatusReply>(OnePasswordChannel.Connect, new TokenRequest(_token.Text ?? string.Empty));
        _connect.IsEnabled = true;
        if (status is { State: "Connected" })
        {
            _token.Text = string.Empty;
            _result.Children.Clear();
            _connect.IsVisible = false;
        }
        else if (status is { Error: { } error })
        {
            _result.Children.Add(_Notice($"Not connected: {error}.", isError: true));
        }

        await _RefreshAsync();
    }

    private async Task<T?> _CallAsync<T>(string action, object? payload)
    {
        try
        {
            var element = await _channel.InvokeAsync(action, JsonSerializer.SerializeToElement(payload ?? true, OnePasswordChannel.Json));
            return element.Deserialize<T>(OnePasswordChannel.Json);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            _connected.Text = "The 1Password plugin is not running.";
            return default;
        }
    }

    private static TextBlock _Text(string text, double size) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };

    private static TextBlock _Heading(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

    private static Border _Notice(string text, bool isError, Control? below = null) => new()
    {
        Padding = new Thickness(10),
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.Parse(isError ? "#D9534F" : "#4CAF50")),
        Child = new StackPanel { Spacing = 6, Children = { _Text(text, 12), below ?? new Panel() } },
    };

    // The page's view of the host's context, over the channel: the panel only needs the availability, its change event and Open Security.
    private sealed class ChannelVaultContext : IVaultProviderContext
    {
        private readonly IPluginUiChannel _channel;
        private VaultAvailability _availability = VaultAvailability.EncryptionOff;

        public ChannelVaultContext(IPluginUiChannel channel)
        {
            _channel = channel;
            channel.Subscribe(OnePasswordChannel.AvailabilityChanged, channelEvent =>
            {
                if (Enum.TryParse<VaultAvailability>(channelEvent.Payload.GetString(), out var availability))
                {
                    Availability = availability;
                }
            });
        }

        public VaultAvailability Availability
        {
            get => _availability;
            set
            {
                _availability = value;
                AvailabilityChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? AvailabilityChanged;

        public bool TryStoreCredential(string value) => false;

        public string? ReadCredential() => null;

        public void ClearCredential()
        {
        }

        public void OpenEncryptionSettings() => _ = _channel.InvokeAsync(OnePasswordChannel.OpenSecurity, JsonSerializer.SerializeToElement(true));
    }
}
