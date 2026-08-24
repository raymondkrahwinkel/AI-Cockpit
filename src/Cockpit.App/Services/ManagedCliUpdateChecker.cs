using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.ManagedCli;

namespace Cockpit.App.Services;

// Periodically checks each installed managed CLI (AC-20) against its provider's latest version. Auto-update
// (AC-767, default on) installs and toasts; off, it toasts once and leaves installing to the config button.
// Never nags: an "available" toast fires once per (cli, version), and offline/edge failures are swallowed.
public sealed class ManagedCliUpdateChecker(
    IManagedCliService managedCli,
    IManagedCliAutoUpdateStore autoUpdateStore,
    IToastService toastService,
    ILogger<ManagedCliUpdateChecker> logger) : ISingletonService
{
    // (CliName, LatestVersion) pairs already toasted "available" this run — a later tick only announces a version
    // beyond what is already here, never the same one twice. Not used for the auto-update success toast: once
    // installed, InstalledVersion catches up to LatestVersion and the same pair simply stops qualifying.
    private readonly HashSet<(string CliName, string LatestVersion)> _notified = [];

    // With a download in the loop a pass can take minutes (vs. the ~20 s status-only check before AC-767). A tick
    // that lands while a previous pass is still running skips outright rather than queueing — two installs of the
    // same CLI at once would race each other's `.download` staging directory.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            foreach (var cliName in managedCli.RegisteredCliNames)
            {
                await _CheckOneAsync(cliName, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task _CheckOneAsync(string cliName, CancellationToken cancellationToken)
    {
        try
        {
            var status = await managedCli.GetStatusAsync(cliName, cancellationToken).ConfigureAwait(false);

            // Not installed, or the channel could not be reached — nothing to do (never a false "outdated", and
            // never an install of a CLI the operator never turned on).
            if (string.IsNullOrEmpty(status.InstalledVersion) || string.IsNullOrEmpty(status.LatestVersion))
            {
                return;
            }

            if (!Version.TryParse(status.InstalledVersion, out var installed)
                || !Version.TryParse(status.LatestVersion, out var latest)
                || latest <= installed)
            {
                return; // up to date
            }

            if (await autoUpdateStore.IsEnabledAsync(cliName, cancellationToken).ConfigureAwait(false))
            {
                await _AutoUpdateAsync(cliName, status.InstalledVersion, status.LatestVersion, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _ToastAvailable(cliName, status.InstalledVersion, status.LatestVersion);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fail-silent per CLI: an offline/edge failure must not crash the app or break the timer loop.
            logger.LogDebug(exception, "Managed CLI '{CliName}' update check failed; skipping this pass.", cliName);
        }
    }

    private async Task _AutoUpdateAsync(string cliName, string installedVersion, string latestVersion, CancellationToken cancellationToken)
    {
        var result = await managedCli.EnsureInstalledAsync(cliName, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            toastService.Show($"{cliName}: {installedVersion} → {result.Version}. Updated automatically.", ToastSeverity.Information);
        }
        else
        {
            // EnsureInstalledAsync never throws — offline, a checksum mismatch, a network hiccup — and reports it
            // rather than leaving the operator with no explanation. Fall back to the plain "available" toast; the
            // next tick tries the install again.
            _ToastAvailable(cliName, installedVersion, latestVersion);
        }
    }

    private void _ToastAvailable(string cliName, string installedVersion, string latestVersion)
    {
        if (_notified.Add((cliName, latestVersion)))
        {
            toastService.Show(
                $"A newer {cliName} is available: {installedVersion} → {latestVersion}. Update it in a {cliName} profile's settings.",
                ToastSeverity.Information);
        }
    }
}
