using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

// AC-1434: what the plugin screens do to the installed plugins, and nothing more. Every change takes effect at the
// next start; the host's plugin contract version is the implementation's to know, not the caller's.
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
    Task<PluginProvisionResult> InstallFromStoreAsync(PluginProvisionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables a plugin; enabling trusts exactly the bytes <paramref name="pinnedSha256"/> names.
    /// </summary>
    Task SetEnabledAsync(string folderId, bool enabled, string pinnedSha256, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uninstalls a plugin at the next start and forgets its registration.
    /// </summary>
    Task RemoveAsync(string folderId, CancellationToken cancellationToken = default);
}
