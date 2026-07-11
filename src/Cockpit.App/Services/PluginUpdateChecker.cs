using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Plugins;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Services;

/// <summary>
/// Real <see cref="IPluginUpdateChecker"/> (#59): compares every installed plugin's version against the
/// `latestVersion` its configured store(s) advertise (<see cref="PluginVersion.IsNewer"/>, the same
/// comparison the manual "Browse stores" flow in <see cref="PluginManagerViewModel"/> uses) and toasts a
/// summary once a newly detected update appears. Never toasts twice for the same (plugin, version) pair —
/// only a version bump beyond what was already reported toasts again, so the 15-minute timer in
/// <see cref="App"/> doesn't nag on every tick.
/// </summary>
/// <remarks>
/// The installed-plugin lookup is an injectable delegate (defaulting to the real
/// <see cref="PluginBootstrap.DiscoverAsync"/>) rather than a direct <see cref="PluginBootstrap"/> call,
/// mirroring <see cref="AppRestartService"/>'s delegate-seam pattern — <see cref="PluginBootstrap"/> is a
/// sealed class with no interface, so a test needs this seam to supply a fixed installed set instead of
/// touching the real plugins folder on disk.
/// </remarks>
public sealed class PluginUpdateChecker : IPluginUpdateChecker, ISingletonService
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<DiscoveredPlugin>>> _getInstalledPluginsAsync;
    private readonly IPluginStoreConfigStore _storeConfigStore;
    private readonly IPluginStoreClient _storeClient;
    private readonly IToastService _toastService;
    private readonly CockpitViewModel _cockpit;
    private readonly ILogger<PluginUpdateChecker> _logger;

    // (FolderId, LatestVersion) pairs already toasted this run — a later 15-minute pass only toasts a
    // version bump beyond what is already in here, never the same update twice.
    private readonly HashSet<(string FolderId, string LatestVersion)> _notifiedUpdates = [];

    public PluginUpdateChecker(
        PluginBootstrap bootstrap,
        IPluginStoreConfigStore storeConfigStore,
        IPluginStoreClient storeClient,
        IToastService toastService,
        CockpitViewModel cockpit,
        ILogger<PluginUpdateChecker> logger)
        : this(
            cancellationToken => bootstrap.DiscoverAsync(AbstractionsContract.Version, cancellationToken),
            storeConfigStore,
            storeClient,
            toastService,
            cockpit,
            logger)
    {
    }

    internal PluginUpdateChecker(
        Func<CancellationToken, Task<IReadOnlyList<DiscoveredPlugin>>> getInstalledPluginsAsync,
        IPluginStoreConfigStore storeConfigStore,
        IPluginStoreClient storeClient,
        IToastService toastService,
        CockpitViewModel cockpit,
        ILogger<PluginUpdateChecker> logger)
    {
        _getInstalledPluginsAsync = getInstalledPluginsAsync;
        _storeConfigStore = storeConfigStore;
        _storeClient = storeClient;
        _toastService = toastService;
        _cockpit = cockpit;
        _logger = logger;
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var updates = await _FindUpdatesAsync(cancellationToken).ConfigureAwait(false);
            _NotifyNewUpdates(updates);
        }
        catch (Exception exception)
        {
            // Fail-silent: a store network/parse failure (or a discovery I/O error) must never crash the
            // app or break the 15-minute timer loop — just skip this pass and try again next tick.
            _logger.LogWarning(exception, "Plugin update check failed; skipping this pass.");
        }
    }

    private async Task<IReadOnlyList<PluginUpdateInfo>> _FindUpdatesAsync(CancellationToken cancellationToken)
    {
        var installed = await _getInstalledPluginsAsync(cancellationToken).ConfigureAwait(false);
        var stores = await _storeConfigStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        var updates = new List<PluginUpdateInfo>();
        var seenFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var storeUrl in stores)
        {
            var fetch = await _storeClient.FetchIndexAsync(storeUrl, cancellationToken).ConfigureAwait(false);
            if (!fetch.IsSuccess || fetch.Index is null)
            {
                continue;
            }

            foreach (var entry in fetch.Index.Plugins)
            {
                var folderId = PluginFolderName.Normalize(entry.Id);
                // First store wins for a given plugin id, same as the manual "Browse stores" flow.
                if (!seenFolderIds.Add(folderId))
                {
                    continue;
                }

                var plugin = installed.FirstOrDefault(candidate => candidate.FolderId == folderId);
                if (plugin is null)
                {
                    continue; // not installed — nothing to update
                }

                if (PluginVersion.IsNewer(entry.LatestVersion, plugin.Manifest.Version))
                {
                    updates.Add(new PluginUpdateInfo(folderId, plugin.Manifest.Name, plugin.Manifest.Version, entry.LatestVersion));
                }
            }
        }

        return updates;
    }

    private void _NotifyNewUpdates(IReadOnlyList<PluginUpdateInfo> updates)
    {
        var newUpdates = new List<PluginUpdateInfo>();
        foreach (var update in updates)
        {
            if (_notifiedUpdates.Add((update.FolderId, update.LatestVersion)))
            {
                newUpdates.Add(update);
            }
        }

        if (newUpdates.Count == 0)
        {
            return;
        }

        var message = newUpdates.Count == 1
            ? $"Plugin update available: {newUpdates[0].Name} {newUpdates[0].InstalledVersion} → {newUpdates[0].LatestVersion}"
            : $"{newUpdates.Count} plugin updates available";

        _toastService.Show(message, ToastSeverity.Information, "View", () => _ = _cockpit.OpenPluginStoreUpdatesAsync());
    }
}
