using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Proxmox.Engine;
using Cockpit.Plugin.Proxmox.Settings;

namespace Cockpit.Plugin.Proxmox.Ui;

// The plugin's settings view: one Proxmox target (host, port, API token), its certificate trust, and the two
// off-by-default capabilities. All code-behind Avalonia, like the other plugins. The API token box is never
// prefilled with the stored value — a blank box keeps what is already stored, typing replaces it.
internal sealed class ProxmoxSettingsControl : UserControl, IPluginSettingsView
{
    private readonly ProxmoxSettings _settings;
    private readonly CheckBox _mcpEnabled;
    private readonly TextBox _host;
    private readonly TextBox _port;
    private readonly TextBox _tokenId;
    private readonly TextBox _apiToken;
    private readonly TextBlock _fingerprintText;
    private readonly Button _trustButton;
    private readonly CheckBox _allowRollback;
    private readonly CheckBox _allowDelete;

    private string? _fetchedFingerprint;
    private string? _pendingTrustedFingerprint;

    public ProxmoxSettingsControl(ICockpitHost host, ProxmoxSettings settings)
    {
        _settings = settings;
        _pendingTrustedFingerprint = settings.TrustedCertFingerprint is { Length: > 0 } stored ? stored : null;

        _mcpEnabled = new CheckBox { Content = "Offer the cockpit-proxmox MCP server to sessions", IsChecked = settings.McpEnabled };

        _host = new TextBox { Text = settings.Host, PlaceholderText = "e.g. pve.example.lan" };
        _port = new TextBox { Text = settings.Port.ToString(), PlaceholderText = "8006", Width = 100 };
        _tokenId = new TextBox { Text = settings.TokenId, PlaceholderText = "user@realm!tokenid" };
        _apiToken = new TextBox
        {
            PasswordChar = '•',
            PlaceholderText = settings.IsConfigured ? "Leave blank to keep the stored token, or type a new one to replace it" : "Token UUID",
        };

        _fingerprintText = new TextBlock { Text = _FingerprintLabel(), FontSize = 11, TextWrapping = TextWrapping.Wrap };
        var showFingerprint = new Button { Content = "Show fingerprint" };
        _trustButton = new Button { Content = "Trust this certificate", IsEnabled = false, Margin = new(6, 0, 0, 0) };
        showFingerprint.Click += async (_, _) => await _ShowFingerprintAsync(showFingerprint);
        _trustButton.Click += (_, _) => _TrustFetchedFingerprint();

        _allowRollback = new CheckBox { Content = "Allow rolling back a VM/LXC snapshot (destructive — off by default)", IsChecked = settings.AllowRollback };
        _allowDelete = new CheckBox { Content = "Allow deleting a VM or LXC container (off by default)", IsChecked = settings.AllowDelete };

        var connectionHeading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new(0, 8, 0, 0),
            Children = { new TextBlock { Text = "Proxmox target" }, host.CreateHelpHint("proxmox", "connecting") },
        };

        var portRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _host, _port } };
        var fingerprintButtons = new StackPanel { Orientation = Orientation.Horizontal, Children = { showFingerprint, _trustButton } };

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _mcpEnabled,
                connectionHeading,
                portRow,
                _tokenId,
                _apiToken,
                new TextBlock
                {
                    Text = "The token needs at least PVEAuditor on / to read; a change also needs write access to what it touches. See Datacenter → Permissions in Proxmox.",
                    Opacity = 0.7,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock { Text = "Certificate", Margin = new(0, 6, 0, 0) },
                _fingerprintText,
                fingerprintButtons,
                new TextBlock { Text = "Dangerous capabilities — off by default", Margin = new(0, 6, 0, 0) },
                _allowRollback,
                _allowDelete,
            },
        };
    }

    public bool TryStage(out Action? commit, out string? error)
    {
        if (!int.TryParse(_port.Text, out var port) || port is <= 0 or > 65535)
        {
            commit = null;
            error = "The port must be a number between 1 and 65535.";
            return false;
        }

        commit = _Commit;
        error = null;
        return true;
    }

    private void _Commit()
    {
        _settings.Host = (_host.Text ?? string.Empty).Trim();
        _settings.Port = int.TryParse(_port.Text, out var port) ? port : 8006;
        _settings.TokenId = (_tokenId.Text ?? string.Empty).Trim();

        var typedToken = _apiToken.Text ?? string.Empty;
        if (typedToken.Length > 0)
        {
            _settings.ApiToken = typedToken;
        }

        if (_pendingTrustedFingerprint is not null)
        {
            _settings.TrustedCertFingerprint = _pendingTrustedFingerprint;
        }

        _settings.AllowRollback = _allowRollback.IsChecked ?? false;
        _settings.AllowDelete = _allowDelete.IsChecked ?? false;
        _settings.McpEnabled = _mcpEnabled.IsChecked ?? true;
    }

    private async Task _ShowFingerprintAsync(Button trigger)
    {
        var host = (_host.Text ?? string.Empty).Trim();
        if (host.Length == 0 || !int.TryParse(_port.Text, out var port))
        {
            _fingerprintText.Text = "Enter a host and port first.";
            return;
        }

        trigger.IsEnabled = false;
        _trustButton.IsEnabled = false;
        _fingerprintText.Text = "Connecting…";

        var (fingerprint, error) = await ProxmoxCertificateProbe.FetchFingerprintAsync(host, port, CancellationToken.None);

        trigger.IsEnabled = true;
        if (fingerprint is null)
        {
            _fetchedFingerprint = null;
            _fingerprintText.Text = error ?? "Could not read the certificate.";
            return;
        }

        _fetchedFingerprint = fingerprint;
        _trustButton.IsEnabled = true;
        _fingerprintText.Text = $"Presented fingerprint (SHA-256):\n{fingerprint}\n\nVerify this matches your Proxmox host's certificate (shown on its own console/GUI) before trusting it.";
    }

    private void _TrustFetchedFingerprint()
    {
        if (_fetchedFingerprint is null)
        {
            return;
        }

        _pendingTrustedFingerprint = _fetchedFingerprint;
        _trustButton.IsEnabled = false;
        _fingerprintText.Text = $"Will be trusted on save (SHA-256):\n{_fetchedFingerprint}";
    }

    private string _FingerprintLabel() => _pendingTrustedFingerprint is { } fingerprint
        ? $"Currently trusted (SHA-256):\n{fingerprint}"
        : "No certificate trusted yet — enter the host and port above, then \"Show fingerprint\" to read it.";
}
