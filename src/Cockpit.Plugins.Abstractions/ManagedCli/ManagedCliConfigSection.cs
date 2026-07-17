using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugins.Abstractions.ManagedCli;

/// <summary>
/// The reusable "Install / Update / Remove" panel a provider config view embeds so the operator can let the cockpit
/// manage the provider's CLI (AC-20). Drives the host's generic installer through <see cref="ICockpitHost"/>: shows
/// whether a managed copy is installed and where, installs the latest on demand, and removes it to fall back to a
/// pinned path or PATH. Nothing here is required — it is a convenience beside the manual executable-path field.
/// <para>
/// Deliberately in the shared abstractions assembly (like <see cref="Sessions.ProviderConfigStatus"/>): every provider
/// that registers a managed CLI needs exactly this affordance, so centralising it keeps the panel identical across
/// providers instead of each plugin hand-rolling it.
/// </para>
/// </summary>
public sealed class ManagedCliConfigSection
{
    private readonly ICockpitHost _host;
    private readonly string _cliName;
    private readonly string _displayName;
    private readonly Action? _onStateChanged;
    private readonly TextBlock _status = ProviderConfigStatus.CreateLine();
    private readonly Button _installButton = new() { Content = "Install" };
    private readonly Button _removeButton = new() { Content = "Remove", IsVisible = false };

    /// <summary>The control to place in the provider config view's field stack.</summary>
    public Control View { get; }

    /// <param name="onStateChanged">
    /// Invoked (on the UI thread) after an install or remove changes what is on disk, so the host config view can
    /// refresh its own "what will run" line — keeping the executable-status and this panel from disagreeing.
    /// </param>
    public ManagedCliConfigSection(ICockpitHost host, string cliName, string displayName, Action? onStateChanged = null)
    {
        _host = host;
        _cliName = cliName;
        _displayName = displayName;
        _onStateChanged = onStateChanged;

        _installButton.Click += async (_, _) => await _InstallAsync();
        _removeButton.Click += (_, _) => _Remove();

        View = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = $"Managed {displayName} (optional)", FontSize = 11 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _installButton, _removeButton },
                },
                _status,
            },
        };

        _Refresh();
    }

    private void _Refresh()
    {
        var managedPath = _host.ResolveManagedCliPath(_cliName);
        if (!string.IsNullOrEmpty(managedPath))
        {
            ProviderConfigStatus.Set(_status, $"Installed and used automatically: {managedPath}", isOk: true);
            _installButton.Content = "Update";
            _removeButton.IsVisible = true;
        }
        else
        {
            ProviderConfigStatus.Set(_status, $"Not installed — {_displayName} is resolved from a pinned path or PATH. Install to let the cockpit manage it.", isOk: true);
            _installButton.Content = "Install";
            _removeButton.IsVisible = false;
        }
    }

    private async Task _InstallAsync()
    {
        _installButton.IsEnabled = false;
        _removeButton.IsEnabled = false;
        ProviderConfigStatus.Set(_status, $"Downloading {_displayName}… this can take a minute.", isOk: true);

        // InstallManagedCliAsync never throws — an offline machine or a checksum mismatch comes back as an
        // unsuccessful result, so a failed install just leaves the operator on PATH rather than breaking the dialog.
        var result = await _host.InstallManagedCliAsync(_cliName).ConfigureAwait(true);

        _installButton.IsEnabled = true;
        _removeButton.IsEnabled = true;

        if (result.Success)
        {
            _host.ShowToast($"{_displayName} {result.Version} installed.", PluginToastSeverity.Success);
            _Refresh();
            _onStateChanged?.Invoke();
        }
        else
        {
            ProviderConfigStatus.Set(_status, result.Error ?? $"Could not install {_displayName}.", isOk: false);
            _host.ShowToast($"Could not install {_displayName}.", PluginToastSeverity.Error);
            _removeButton.IsVisible = !string.IsNullOrEmpty(_host.ResolveManagedCliPath(_cliName));
        }
    }

    private void _Remove()
    {
        if (_host.RemoveManagedCli(_cliName))
        {
            _host.ShowToast($"Managed {_displayName} removed.", PluginToastSeverity.Information);
        }

        _Refresh();
        _onStateChanged?.Invoke();
    }
}
