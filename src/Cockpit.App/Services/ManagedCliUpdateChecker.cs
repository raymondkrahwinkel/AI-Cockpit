using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.ManagedCli;

namespace Cockpit.App.Services;

/// <summary>
/// Periodically checks each installed managed CLI (AC-20) against the latest version its provider offers and toasts
/// once when a newer one appears — the background half of the config view's on-demand update check. Generic: it
/// iterates the CLI names plugins registered and asks <see cref="IManagedCliService.GetStatusAsync"/>; how a version
/// is discovered stays in the provider's descriptor, never here.
/// </summary>
/// <remarks>
/// Never nags: a given (cli, version) is announced once per run, an uninstalled or up-to-date CLI says nothing, and an
/// offline/edge failure is swallowed so the timer loop survives — mirroring <see cref="PluginUpdateChecker"/>.
/// </remarks>
public sealed class ManagedCliUpdateChecker(
    IManagedCliService managedCli,
    IToastService toastService,
    ILogger<ManagedCliUpdateChecker> logger) : ISingletonService
{
    // (CliName, LatestVersion) pairs already toasted this run — a later tick only announces a version beyond what is
    // already here, never the same update twice.
    private readonly HashSet<(string CliName, string LatestVersion)> _notified = [];

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        foreach (var cliName in managedCli.RegisteredCliNames)
        {
            try
            {
                var status = await managedCli.GetStatusAsync(cliName, cancellationToken).ConfigureAwait(false);

                // Not installed, or the channel could not be reached — nothing to announce (never a false "outdated").
                if (string.IsNullOrEmpty(status.InstalledVersion) || string.IsNullOrEmpty(status.LatestVersion))
                {
                    continue;
                }

                if (!Version.TryParse(status.InstalledVersion, out var installed)
                    || !Version.TryParse(status.LatestVersion, out var latest)
                    || latest <= installed)
                {
                    continue; // up to date
                }

                if (_notified.Add((cliName, status.LatestVersion)))
                {
                    toastService.Show(
                        $"A newer {cliName} is available: {status.InstalledVersion} → {status.LatestVersion}. Update it in a {cliName} profile's settings.",
                        ToastSeverity.Information);
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
    }
}
