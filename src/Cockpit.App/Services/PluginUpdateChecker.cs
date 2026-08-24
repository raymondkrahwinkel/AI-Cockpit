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

// Real `IPluginUpdateChecker` (#59): compares each plugin's version against its store's `latestVersion`
// and toasts a summary once, never twice for the same (plugin, version) pair. The installed-plugin lookup
// is an injectable delegate, not a direct `PluginBootstrap` call, since it's sealed with no interface and tests need a fixed set instead of the real plugins folder.
public sealed class PluginUpdateChecker : IPluginUpdateChecker, ISingletonService
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<DiscoveredPlugin>>> _getInstalledPluginsAsync;
    // (entry id, latest version) -> whether the operator already staged that update this session (AC-76); an
    // injectable seam over PluginManagerViewModel.IsUpdateStaged so the exclusion is unit-testable without a dispatcher.
    private readonly Func<string, string, bool> _isUpdateStaged;
    private readonly IPluginStoreConfigStore _storeConfigStore;
    private readonly IPluginStoreClient _storeClient;
    private readonly IToastService _toastService;
    private readonly CockpitViewModel _cockpit;
    private readonly ILogger<PluginUpdateChecker> _logger;
    private readonly Version _hostVersion;

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
        ILogger<PluginUpdateChecker> logger,
        Func<string, string, bool>? isUpdateStaged = null,
        Version? hostVersion = null)
    {
        _getInstalledPluginsAsync = getInstalledPluginsAsync;
        _isUpdateStaged = isUpdateStaged ?? ((entryId, latestVersion) => cockpit.Plugins.IsUpdateStaged(entryId, latestVersion));
        _storeConfigStore = storeConfigStore;
        _storeClient = storeClient;
        _toastService = toastService;
        _cockpit = cockpit;
        _logger = logger;
        _hostVersion = hostVersion ?? HostVersionInfo.Current;
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var updates = await _FindUpdatesAsync(cancellationToken).ConfigureAwait(false);
            // AC-76: the full current set feeds the persistent sidebar badge (not just the newly-detected ones the
            // toast dedups), so an update stays visible in the main window until it is installed.
            _cockpit.Plugins.SetUpdateBadgeCount(updates.Count);
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

        foreach (var store in stores)
        {
            var fetch = await _storeClient.FetchIndexAsync(store, cancellationToken).ConfigureAwait(false);
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

                // Newer, unless already staged this session (AC-76: must not re-inflate the badge), and only
                // when this host can actually run it (AC-181) — same reason
                // PluginManagerViewModel.CanUpdate excludes it from "Update all".
                if (PluginVersion.IsNewer(entry.LatestVersion, plugin.Manifest.Version)
                    && !_isUpdateStaged(entry.Id, entry.LatestVersion)
                    && _HostCanRun(entry, _hostVersion))
                {
                    updates.Add(new PluginUpdateInfo(folderId, plugin.Manifest.Name, plugin.Manifest.Version, entry.LatestVersion));
                }
            }
        }

        return updates;
    }

    // Mirrors StorePluginRowViewModel's own compatibility check (AC-181) — the same PluginLoadPolicy gate the
    // install- and load-time checks apply, so the background badge/toast can never disagree with what an actual
    // install attempt on this host would do.
    private static bool _HostCanRun(PluginStoreEntry entry, Version hostVersion)
    {
        var version = entry.Versions?.FirstOrDefault(v => v.Version == entry.LatestVersion) ?? entry.Versions?.FirstOrDefault();
        if (version is null)
        {
            return true;
        }

        return (version.AbstractionsVersion is not { } abstractionsVersion || abstractionsVersion == AbstractionsContract.Version)
            && PluginLoadPolicy.MeetsMinHostVersion(version.MinHostVersion, hostVersion);
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
