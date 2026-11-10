using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
    private readonly ListBox _list = new() { Classes = { "subnav" }, MinHeight = 110, MaxHeight = 220 };
    private readonly TextBlock _problem = new() { Classes = { "optionsHint", "error" }, IsVisible = false };
    private readonly CheckBox _mcp;
    private readonly TextBox _name = new() { PlaceholderText = "e.g. production" };
    private readonly ComboBox _kind = new() { ItemsSource = Enum.GetValues<ProxmoxServerKind>() };
    private readonly TextBox _hostName = new() { PlaceholderText = "e.g. pve.example.lan" };
    private readonly TextBox _port = new() { Width = 100, PlaceholderText = "8006" };
    private readonly TextBox _tokenId = new() { PlaceholderText = "user@realm!tokenid" };
    private readonly TextBox _token = new() { PasswordChar = '•', PlaceholderText = "Leave blank to keep the stored token" };
    private readonly TextBlock _fingerprint = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _rollback = new() { Content = "Allow snapshot rollback (destructive)" };
    private readonly CheckBox _delete = new() { Content = "Allow VM/LXC deletion (destructive)" };
    private int _selected;
    // True while the list or the fields are being filled from `_servers`: the controls fire their change events
    // for that too, and handling those would write one server's fields over another (AC-2260).
    private bool _loading;
    private bool _showProblems;
    private string? _pendingFingerprint;
    private string? _fetchedFingerprint;

    public ProxmoxSettingsControl(ICockpitUiHost host, ProxmoxSettings settings)
    {
        _host = host;
        _settings = settings;
        _servers = settings.Servers.ToList();
        _tokens = _servers.ToDictionary(server => server.Id, server => settings.GetApiToken(server.Id));

        _mcp = new CheckBox { Content = "Offer the cockpit-proxmox MCP server to sessions", IsChecked = settings.McpEnabled };
        _kind.SelectionChanged += (_, _) =>
        {
            if (!_loading && _kind.SelectedItem is ProxmoxServerKind kind && string.IsNullOrWhiteSpace(_hostName.Text))
            {
                _port.Text = kind == ProxmoxServerKind.Pbs ? "8007" : "8006";
            }
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (!_loading && _list.SelectedIndex >= 0 && _list.SelectedIndex != _selected)
            {
                _Capture();
                _selected = _list.SelectedIndex;
                _Load();
            }
        };
        _kind.ItemTemplate = new FuncDataTemplate<ProxmoxServerKind>((kind, _) => new TextBlock { Text = _KindLabel(kind) });
        _list.ItemTemplate = new FuncDataTemplate<ProxmoxServer>((server, _) => server is null ? new Panel() : _Row(server));
        foreach (var field in new[] { _name, _hostName, _port })
        {
            field.TextChanged += (_, _) => _OnEdited();
        }

        var add = new Button { Content = "Add server" };
        add.Click += (_, _) =>
        {
            _Capture();
            _servers.Add(_NewServer());
            _selected = _servers.Count - 1;
            _RefreshList();
            _Load();
            _name.Focus();
        };
        var remove = new Button { Content = "Remove selected", Margin = new(6, 0, 0, 0) };
        remove.Click += (_, _) =>
        {
            if (_servers.Count == 0)
            {
                return;
            }

            _Capture();
            _tokens.Remove(_servers[_selected].Id);
            _servers.RemoveAt(_selected);
            _selected = _servers.Count == 0 ? -1 : Math.Min(_selected, _servers.Count - 1);
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
                _problem,
                _Field("Name", _name),
                _Field("Type", _kind),
                _HostAndPort(),
                _Field("Token ID", _tokenId),
                _Field("API token", _token),
                new TextBlock { Text = "PVE reads need PVEAuditor; PBS reads need DatastoreAudit. PBS verify and GC need DatastoreVerify and DatastoreModify.", Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap },
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
        var first = _servers.Select((server, index) => (index, problem: _Problem(server))).FirstOrDefault(row => row.problem is not null);
        if (first.problem is not null)
        {
            _showProblems = true;
            _selected = first.index;
            _RefreshList();
            _Load();
            commit = null;
            error = $"{_DisplayName(_servers[first.index])}: {first.problem}";
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
        if (_selected < 0 || _servers.Count == 0)
        {
            return;
        }

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
            Kind = _kind.SelectedItem is ProxmoxServerKind kind ? kind : ProxmoxServerKind.Pve,
        };
        if (!string.IsNullOrWhiteSpace(_token.Text))
        {
            _tokens[current.Id] = _token.Text;
        }
    }

    private void _Load()
    {
        _loading = true;
        try
        {
            _LoadFields();
        }
        finally
        {
            _loading = false;
        }

        _ShowProblem();
    }

    private void _LoadFields()
    {
        if (_selected < 0 || _servers.Count == 0)
        {
            _name.Text = string.Empty;
            _hostName.Text = string.Empty;
            _port.Text = "8006";
            _kind.SelectedItem = ProxmoxServerKind.Pve;
            _tokenId.Text = string.Empty;
            _token.Text = string.Empty;
            _fingerprint.Text = "No servers configured.";
            _rollback.IsChecked = false;
            _delete.IsChecked = false;
            return;
        }

        var server = _servers[_selected];
        _name.Text = server.Name;
        _kind.SelectedItem = server.Kind;
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
        _loading = true;
        try
        {
            _list.ItemsSource = _servers.ToList();
            _list.SelectedIndex = _servers.Count == 0 ? -1 : _selected;
        }
        finally
        {
            _loading = false;
        }
    }

    // A typed name/host/port shows up in the list straight away, and a problem already flagged clears once fixed.
    private void _OnEdited()
    {
        if (_loading || _selected < 0 || _servers.Count == 0)
        {
            return;
        }

        _Capture();
        _RefreshList();
        _ShowProblem();
    }

    private void _ShowProblem()
    {
        var problem = _showProblems && _selected >= 0 && _selected < _servers.Count ? _Problem(_servers[_selected]) : null;
        _problem.Text = problem;
        _problem.IsVisible = problem is not null;
    }

    private Control _Row(ProxmoxServer server)
    {
        var problem = _showProblems ? _Problem(server) : null;
        var detail = new TextBlock
        {
            Classes = { "optionsHint" },
            Text = problem ?? $"{_KindLabel(server.Kind)} · {(string.IsNullOrWhiteSpace(server.Host) ? "no host yet" : $"{server.Host}:{server.Port}")}",
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (problem is not null)
        {
            detail.Classes.Add("error");
        }

        return new StackPanel { Children = { new TextBlock { Text = _DisplayName(server), TextTrimming = TextTrimming.CharacterEllipsis }, detail } };
    }

    // The one thing wrong with a server, if anything: a name is required and unique, a host and a valid port too.
    private string? _Problem(ProxmoxServer server) =>
        string.IsNullOrWhiteSpace(server.Name) ? "Give this server a name."
        : _servers.Count(other => string.Equals(other.Name, server.Name, StringComparison.OrdinalIgnoreCase)) > 1 ? "Another server already uses this name."
        : string.IsNullOrWhiteSpace(server.Host) ? "Enter a host."
        : server.Port is <= 0 or > 65535 ? "The port must be between 1 and 65535."
        : null;

    private static string _DisplayName(ProxmoxServer server) => string.IsNullOrWhiteSpace(server.Name) ? "New server" : server.Name;

    private static string _KindLabel(ProxmoxServerKind kind) => kind == ProxmoxServerKind.Pbs ? "Proxmox Backup Server" : "Proxmox VE";

    private static StackPanel _Field(string label, Control field) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 }, field },
    };

    private Grid _HostAndPort()
    {
        var host = _Field("Host", _hostName);
        var port = _Field("Port", _port);
        Grid.SetColumn(port, 1);
        return new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { host, port } };
    }

    private static ProxmoxServer _NewServer() => new(Guid.NewGuid().ToString("N"), string.Empty, string.Empty, 8006, string.Empty, string.Empty, false, false);
}
