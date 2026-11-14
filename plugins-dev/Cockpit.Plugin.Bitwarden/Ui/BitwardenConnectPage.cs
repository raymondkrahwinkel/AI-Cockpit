using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Vault;

namespace Cockpit.Plugin.Bitwarden.UI;

// Mockup 4 (AC-2333): test a machine-account access token, see which projects it reads, connect only when that is exactly one.
// The token is sent to the backend once, on Test or Connect, and is never kept here after that.
internal sealed class BitwardenConnectPage : UserControl
{
    private const string InstallPage = "https://bitwarden.com/help/secrets-manager-cli/";

    private readonly IPluginUiChannel _channel;
    private readonly ChannelVaultContext _context;
    private readonly TextBox _token = new() { PasswordChar = '•', PlaceholderText = "0.…", MinWidth = 320 };
    private readonly Button _test = new() { Content = "Test connection" };
    private readonly Button _connect = new() { IsVisible = false };
    private readonly Button _disconnect = new() { Content = "Disconnect", IsVisible = false };
    private readonly StackPanel _result = new() { Spacing = 6 };
    private readonly TextBlock _connected = _Text(string.Empty, 12.5);
    private TestReply? _tested;

    public BitwardenConnectPage(IPluginUiChannel channel)
    {
        _channel = channel;
        _context = new ChannelVaultContext(channel);
        _connect.Classes.Add("Accent");
        _test.Click += async (_, _) => await _TestAsync();
        _connect.Click += async (_, _) => await _ConnectAsync();
        _disconnect.Click += async (_, _) =>
        {
            await _CallAsync<bool>(BitwardenChannel.Disconnect, null);
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
                _Text("Lets your agents use secrets from one Bitwarden Secrets Manager project through Cockpit's vault. Agents get names; the value goes only into a process when it starts.", 12.5),
                _Text("Use a separate project for agents. Create a project that holds only what agents may use, for example Agents, and move those secrets into it. Then create a machine account in Bitwarden that can read only that project. Never give it your other projects: whatever the token can read, an approved agent process can receive.", 11.5),
                _Heading("1. MACHINE ACCOUNT"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _token, _test } },
                _Text("An access token of a machine account, created in Bitwarden Secrets Manager under Machine accounts › Access tokens. Stored encrypted with your other credentials (Options › Security); agents and the vault MCP can never read it. Needs the Secrets Manager CLI (bws), which Cockpit can install.", 11),
                _Heading("2. WHICH PROJECT IT READS"),
                _connected,
                _result,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _connect, _disconnect } },
            },
        };
        _ = _RefreshAsync();
    }

    private async Task _RefreshAsync()
    {
        if (await _CallAsync<StatusReply>(BitwardenChannel.Status, null) is not { } status)
        {
            return;
        }

        _context.Availability = Enum.TryParse<VaultAvailability>(status.Availability, out var availability) ? availability : VaultAvailability.EncryptionOff;
        _ApplyAvailability();
        var isConnected = status.State == "Connected";
        _disconnect.IsVisible = isConnected;
        _connected.Text = status.State switch
        {
            "Connected" => $"Connected · machine account · reads one project: {status.Project}",
            "Error" => $"Error: {status.Error}",
            _ => "Not connected. Test the connection to see which projects a token can read; nothing is saved until you connect.",
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
        _test.IsEnabled = false;
        _result.Children.Clear();
        _tested = await _CallAsync<TestReply>(BitwardenChannel.Test, new TokenRequest(_token.Text ?? string.Empty));
        _ApplyAvailability();
        if (_tested is not { } reply)
        {
            return;
        }

        if (!reply.Ok)
        {
            _result.Children.Add(_Failure($"Could not check the token: {reply.Error}.", reply.Error));
            return;
        }

        foreach (var project in reply.Projects)
        {
            _result.Children.Add(_Text($"{project.Name} · {project.Secrets} secrets", 12.5));
        }

        if (reply.Projects.Count == 1)
        {
            _result.Children.Add(_Notice($"The only project. This token reads exactly one project. Agents can ask for secrets in {reply.Projects[0].Name}; each secret still needs your consent per session or project.", isError: false));
            _connect.Content = $"Connect project {reply.Projects[0].Name}";
            _connect.IsVisible = true;
        }
        else
        {
            var count = reply.Projects.Count == 0 ? "no project" : $"{reply.Projects.Count} projects";
            _result.Children.Add(_Notice($"This token can read {count}. Connect a machine account that reads one separate project only. Limit it in Bitwarden, or create a new one, then test again.", isError: true));
        }
    }

    private async Task _ConnectAsync()
    {
        _connect.IsEnabled = false;
        var status = await _CallAsync<StatusReply>(BitwardenChannel.Connect, new TokenRequest(_token.Text ?? string.Empty));
        _connect.IsEnabled = true;
        if (status is { State: "Connected" })
        {
            _token.Text = string.Empty;
            _result.Children.Clear();
            _connect.IsVisible = false;
        }
        else if (status is { Error: { } error })
        {
            _result.Children.Add(_Failure($"Not connected: {error}.", error));
        }

        await _RefreshAsync();
    }

    private async Task<T?> _CallAsync<T>(string action, object? payload)
    {
        try
        {
            var element = await _channel.InvokeAsync(action, JsonSerializer.SerializeToElement(payload ?? true, BitwardenChannel.Json));
            return element.Deserialize<T>(BitwardenChannel.Json);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            _connected.Text = "The Bitwarden plugin is not running.";
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

    // A missing `bws` says what to install and where to get it; any other reason is shown as it is.
    private Border _Failure(string text, string? reason)
    {
        if (reason != "bws CLI missing")
        {
            return _Notice(text, isError: true);
        }

        var install = new Button { Content = "Open the Secrets Manager CLI page", HorizontalAlignment = HorizontalAlignment.Left };
        install.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(new Uri(InstallPage));
            }
        };
        return _Notice($"{text} The Secrets Manager CLI (bws) must be installed on this machine.", isError: true, below: install);
    }

    // The page's view of the host's context, over the channel: the panel only needs the availability, its change event and Open Security.
    private sealed class ChannelVaultContext : IVaultProviderContext
    {
        private readonly IPluginUiChannel _channel;
        private VaultAvailability _availability = VaultAvailability.EncryptionOff;

        public ChannelVaultContext(IPluginUiChannel channel)
        {
            _channel = channel;
            channel.Subscribe(BitwardenChannel.AvailabilityChanged, channelEvent =>
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

        public void OpenEncryptionSettings() => _ = _channel.InvokeAsync(BitwardenChannel.OpenSecurity, JsonSerializer.SerializeToElement(true));
    }
}
