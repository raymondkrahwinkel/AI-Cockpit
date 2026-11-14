using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Installs and removes plugin files, using shadow copies for live updates and startup staging when a restart is required.
/// Enable/consent state lives in <see cref="IPluginRegistrationStore"/>.
/// </summary>
public interface IPluginInstaller
{
    /// <summary>
    /// Validates and unpacks the archive; returns the folder id on success or a reason it was rejected — including
    /// a <c>minHostVersion</c> newer than this cockpit (AC-181), checked here rather than only at load. Updating
    /// an existing install stages the new version for <see cref="SweepPendingUpdatesAsync"/>; <paramref name="hostVersion"/> defaults to <see cref="HostVersionInfo"/>, only overridden explicitly in a test.
    /// </summary>
    Task<PluginInstallResult> InstallFromZipAsync(
        string zipFilePath, int hostAbstractionsMajor, Version? hostVersion = null, CancellationToken cancellationToken = default, string? expectedId = null, string? expectedVersion = null);

    Task<PluginInstalledState> GetInstalledStateAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an installed plugin for removal; deletion follows runtime teardown or the next startup.
    /// </summary>
    Task MarkForRemovalAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes this marked install after its runtime has stopped; false leaves it marked for the next startup.
    /// </summary>
    Task<bool> DeleteMarkedFolderAsync(string folderId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>
    /// Deletes any folders marked for removal; called once at startup before discovery so a removed plugin never loads again.
    /// </summary>
    Task SweepRemovalsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies any staged plugin updates (replacing the old folder with the new version); called once at startup before discovery, so the swap runs while no plugin assembly is loaded/locked.
    /// </summary>
    Task SweepPendingUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts back the plugin's folder from a verified copy of an earlier version, after a live update to a newer one was rolled back.
    /// False when the copy no longer hashes to <paramref name="expectedSha256"/> or the folder could not be replaced; the folder is then as it was.
    /// </summary>
    Task<bool> RestoreFolderAsync(string folderId, string sourceFolder, string expectedSha256, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
