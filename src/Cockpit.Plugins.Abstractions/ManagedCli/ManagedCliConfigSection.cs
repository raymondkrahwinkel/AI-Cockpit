using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
    // A muted, checkmark-less brush for the neutral states (not-installed, downloading) — a green ✓ there would read
    // as a success mark on something that has not happened. The green ✓ is kept only for the actually-installed state.
    private static readonly IBrush _MutedBrush = new SolidColorBrush(Color.Parse("#9AA0A6"));

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
        _ = _RefreshUpdateStateAsync();
    }

    private void _Refresh()
    {
        _installButton.IsEnabled = true;
        if (!string.IsNullOrEmpty(_host.ResolveManagedCliPath(_cliName)))
        {
            // Which copy runs (and its path) is stated by the config view's executable line; here just confirm the
            // install, so the two lines complement rather than repeat each other. The update check below refines the
            // button ("Up to date" / "Update to X") once the provider's latest version is known.
            ProviderConfigStatus.Set(_status, "Installed", isOk: true);
            _installButton.Content = "Update";
            _removeButton.IsVisible = true;
        }
        else
        {
            _SetMuted($"Not installed. Install to let Cockpit download and manage {_displayName}.");
            _installButton.Content = "Install";
            _removeButton.IsVisible = false;
        }
    }

    // Ask the provider whether the installed copy is the latest, so "Update" is offered only when a newer version
    // actually exists and "Up to date" (disabled) is shown otherwise — not an Update button that may do nothing. A
    // channel that cannot be reached leaves the plain "Update" fallback rather than a false "up to date".
    private async Task _RefreshUpdateStateAsync()
    {
        if (string.IsNullOrEmpty(_host.ResolveManagedCliPath(_cliName)))
        {
            return;
        }

        _installButton.Content = "Checking…";
        _installButton.IsEnabled = false;

        ManagedCliStatus status;
        try
        {
            status = await _host.GetManagedCliStatusAsync(_cliName).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The check must never leave the button stuck on "Checking…": fall back to a plain, enabled Update
            // (unless it was removed in the meantime).
            if (!string.IsNullOrEmpty(_host.ResolveManagedCliPath(_cliName)))
            {
                _installButton.Content = "Update";
                _installButton.IsEnabled = true;
            }

            return;
        }

        // The operator may have removed it while the check ran.
        if (string.IsNullOrEmpty(_host.ResolveManagedCliPath(_cliName)))
        {
            return;
        }

        _installButton.IsEnabled = true;
        var installed = status.InstalledVersion;

        if (string.IsNullOrEmpty(status.LatestVersion) || string.IsNullOrEmpty(installed))
        {
            _installButton.Content = "Update";
            ProviderConfigStatus.Set(_status, installed is { Length: > 0 } ? $"Installed — {installed}" : "Installed", isOk: true);
            return;
        }

        var updateAvailable = Version.TryParse(installed, out var installedVersion)
            && Version.TryParse(status.LatestVersion, out var latestVersion)
            && latestVersion > installedVersion;

        if (updateAvailable)
        {
            _installButton.Content = $"Update to {status.LatestVersion}";
            ProviderConfigStatus.Set(_status, $"Installed — {installed} · {status.LatestVersion} available", isOk: true);
        }
        else
        {
            _installButton.Content = "Up to date";
            _installButton.IsEnabled = false;
            ProviderConfigStatus.Set(_status, $"Installed — {installed} (latest)", isOk: true);
        }
    }

    private void _SetMuted(string message)
    {
        _status.Text = message;
        _status.Foreground = _MutedBrush;
    }

    private async Task _InstallAsync()
    {
        _installButton.IsEnabled = false;
        _removeButton.IsEnabled = false;
        _SetMuted($"Downloading {_displayName}… this can take a minute.");

        // InstallManagedCliAsync never throws — an offline machine or a checksum mismatch comes back as an
        // unsuccessful result, so a failed install just leaves the operator on PATH rather than breaking the dialog.
        var result = await _host.InstallManagedCliAsync(_cliName).ConfigureAwait(true);

        _installButton.IsEnabled = true;
        _removeButton.IsEnabled = true;

        if (result.Success)
        {
            _host.ShowToast($"{_displayName} {result.Version} installed.", PluginToastSeverity.Success);
            _Refresh();
            _ = _RefreshUpdateStateAsync();
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
