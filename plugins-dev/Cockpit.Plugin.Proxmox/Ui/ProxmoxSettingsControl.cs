using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Proxmox.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Proxmox.UI;

internal sealed class ProxmoxSettingsControl : UserControl, IPluginSettingsView
{
    private readonly ProxmoxSettings _settings;
    private readonly ICockpitUiHost _host;
    private readonly List<ProxmoxServer> _servers;
    private readonly Dictionary<string, string> _tokens;
    private readonly ListBox _list = new() { Height = 110 };
    private readonly CheckBox _mcp;
    private readonly TextBox _name = new() { PlaceholderText = "e.g. production" };
    private readonly TextBox _hostName = new() { PlaceholderText = "e.g. pve.example.lan" };
    private readonly TextBox _port = new() { Width = 100, PlaceholderText = "8006" };
    private readonly TextBox _tokenId = new() { PlaceholderText = "user@realm!tokenid" };
    private readonly TextBox _token = new() { PasswordChar = '•', PlaceholderText = "Leave blank to keep the stored token" };
    private readonly TextBlock _fingerprint = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _rollback = new() { Content = "Allow snapshot rollback (destructive)" };
    private readonly CheckBox _delete = new() { Content = "Allow VM/LXC deletion (destructive)" };
    private int _selected;
    private string? _pendingFingerprint;
    private string? _fetchedFingerprint;

    public ProxmoxSettingsControl(ICockpitUiHost host, ProxmoxSettings settings)
    {
        _host = host;
        _settings = settings;
        _servers = settings.Servers.ToList();
        _tokens = _servers.ToDictionary(server => server.Id, server => settings.GetApiToken(server.Id));
        if (_servers.Count == 0)
        {
            _servers.Add(_NewServer());
        }

        _mcp = new CheckBox { Content = "Offer the cockpit-proxmox MCP server to sessions", IsChecked = settings.McpEnabled };
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedIndex >= 0 && _list.SelectedIndex != _selected)
            {
                _Capture();
                _selected = _list.SelectedIndex;
                _Load();
            }
        };

        var add = new Button { Content = "Add server" };
        add.Click += (_, _) =>
        {
            _Capture();
            _servers.Add(_NewServer());
            _selected = _servers.Count - 1;
            _RefreshList();
            _Load();
        };
        var remove = new Button { Content = "Remove selected", Margin = new(6, 0, 0, 0) };
        remove.Click += (_, _) =>
        {
            _Capture();
            _tokens.Remove(_servers[_selected].Id);
            _servers.RemoveAt(_selected);
            if (_servers.Count == 0)
            {
                _servers.Add(_NewServer());
            }

            _selected = Math.Min(_selected, _servers.Count - 1);
            _RefreshList();
            _Load();
        };
        var show = new Button { Content = "Show fingerprint" };
        show.Click += async (_, _) => await _ShowFingerprintAsync(show);
        var trust = new Button { Content = "Trust shown fingerprint", Margin = new(6, 0, 0, 0) };
        trust.Click += (_, _) =>
        {
            _pendingFingerprint = _fetchedFingerprint;
            _fingerprint.Text = _pendingFingerprint is null ? "No fingerprint was fetched." : $"Will trust SHA-256: {_pendingFingerprint}";
        };

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _mcp,
                new TextBlock { Text = "Proxmox servers", FontWeight = FontWeight.SemiBold },
                _list,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { add, remove } },
                _name,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _hostName, _port } },
                _tokenId,
                _token,
                new TextBlock { Text = "The token needs PVEAuditor on / for reads; changes need write access to the resource they change.", Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Certificate", FontWeight = FontWeight.SemiBold },
                _fingerprint,
                new StackPanel { Orientation = Orientation.Horizontal, Children = { show, trust } },
                _rollback,
                _delete,
            },
        };
        _RefreshList();
        _Load();
    }

    public bool TryStage(out Action? commit, out string? error)
    {
        _Capture();
        if (_servers.Any(server => string.IsNullOrWhiteSpace(server.Name)) || _servers.Select(server => server.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _servers.Count)
        {
            commit = null;
            error = "Every Proxmox server needs a unique name.";
            return false;
        }

        if (_servers.Any(server => string.IsNullOrWhiteSpace(server.Host) || server.Port is <= 0 or > 65535))
        {
            commit = null;
            error = "Every Proxmox server needs a host and a port between 1 and 65535.";
            return false;
        }

        commit = () =>
        {
            _settings.SaveServers(_servers, _tokens);
            _settings.McpEnabled = _mcp.IsChecked ?? true;
        };
        error = null;
        return true;
    }

    private async Task _ShowFingerprintAsync(Button trigger)
    {
        if (!int.TryParse(_port.Text, out var port) || string.IsNullOrWhiteSpace(_hostName.Text))
        {
            _fingerprint.Text = "Enter a host and valid port first.";
            return;
        }

        trigger.IsEnabled = false;
        var answer = await _host.Channel.InvokeAsync(ProxmoxChannel.FetchFingerprint, JsonSerializer.SerializeToElement(new ProxmoxFingerprintRequest(_hostName.Text.Trim(), port), ProxmoxChannel.Json));
        var result = answer.Deserialize<ProxmoxFingerprintAnswer>(ProxmoxChannel.Json);
        _fetchedFingerprint = result?.Fingerprint;
        _fingerprint.Text = _fetchedFingerprint is null ? result?.Error ?? "Could not read the certificate." : $"Presented SHA-256: {_fetchedFingerprint}";
        trigger.IsEnabled = true;
    }

    private void _Capture()
    {
        var current = _servers[_selected];
        _servers[_selected] = current with
        {
            Name = (_name.Text ?? string.Empty).Trim(),
            Host = (_hostName.Text ?? string.Empty).Trim(),
            Port = int.TryParse(_port.Text, out var port) ? port : 0,
            TokenId = (_tokenId.Text ?? string.Empty).Trim(),
            TrustedCertFingerprint = _pendingFingerprint ?? current.TrustedCertFingerprint,
            AllowRollback = _rollback.IsChecked ?? false,
            AllowDelete = _delete.IsChecked ?? false,
        };
        if (!string.IsNullOrWhiteSpace(_token.Text))
        {
            _tokens[current.Id] = _token.Text;
        }
    }

    private void _Load()
    {
        var server = _servers[_selected];
        _name.Text = server.Name;
        _hostName.Text = server.Host;
        _port.Text = server.Port.ToString();
        _tokenId.Text = server.TokenId;
        _token.Text = string.Empty;
        _pendingFingerprint = server.TrustedCertFingerprint;
        _fingerprint.Text = string.IsNullOrWhiteSpace(_pendingFingerprint) ? "No certificate fingerprint trusted." : $"Trusted SHA-256: {_pendingFingerprint}";
        _rollback.IsChecked = server.AllowRollback;
        _delete.IsChecked = server.AllowDelete;
    }

    private void _RefreshList()
    {
        _list.ItemsSource = _servers.Select(server => string.IsNullOrWhiteSpace(server.Name) ? "New server" : server.Name).ToList();
        _list.SelectedIndex = _selected;
    }

    private static ProxmoxServer _NewServer() => new(Guid.NewGuid().ToString("N"), string.Empty, string.Empty, 8006, string.Empty, string.Empty, false, false);
}
