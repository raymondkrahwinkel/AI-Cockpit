using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

// AC-1434: what the plugin screens do to the installed plugins; enabling can be live, every other change waits for the next start.
public interface IPluginAdministration
{
    /// <summary>
    /// The plugins on disk as discovery sees them now, in their menu order, each with its registration and this run's failures.
    /// </summary>
    Task<IReadOnlyList<InstalledPlugin>> GetInstalledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One store's index: the plugins and templates it offers.
    /// </summary>
    Task<PluginStoreFetchResult> FetchStoreIndexAsync(PluginStoreConfig store, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs a plugin archive. An update over an installed plugin is staged and keeps its enabled state, pinned to the new bytes.
    /// </summary>
    Task<PluginInstallResult> InstallFromZipAsync(string zipFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs one store version, the same way <see cref="InstallFromZipAsync"/> installs an archive.
    /// </summary>
    Task<PluginProvisionResult> InstallFromStoreAsync(PluginProvisionRequest request, CancellationToken cancellationToken = default, IProgress<PluginDownloadProgress>? progress = null);

    /// <summary>
    /// Enables or disables a plugin; enabling trusts exactly the bytes <paramref name="pinnedSha256"/> names. Enabling
    /// activates the plugin in the running host when it can, and says otherwise why a restart is needed.
    /// </summary>
    Task<PluginLiveState> SetEnabledAsync(string folderId, bool enabled, string pinnedSha256, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many plugin versions are held in memory that no longer run: a live update leaves the old one until the next start.
    /// </summary>
    Task<int> GetRetiredLoadContextsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets a plugin's registration, deactivates it in the running host when it can, and deletes its folder at the next start.
    /// </summary>
    Task<PluginLiveState> RemoveAsync(string folderId, CancellationToken cancellationToken = default);
}
