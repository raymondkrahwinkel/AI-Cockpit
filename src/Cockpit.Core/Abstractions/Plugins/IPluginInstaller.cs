using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Installs and removes plugins on disk (#14). Installation validates and safely unpacks a <c>.zip</c>
/// into the plugins root; both an update over a loaded plugin and a removal are deferred to the next startup,
/// since a loaded assembly's file stays locked (on Windows) until the process exits. The enable/consent state
/// itself lives in <see cref="IPluginRegistrationStore"/>.
/// </summary>
public interface IPluginInstaller
{
    /// <summary>Validates and unpacks the archive into its own folder under the plugins root; returns the folder id on success or a reason it was rejected. Updating an existing install stages the new version and applies it at the next startup (see <see cref="SweepPendingUpdatesAsync"/>).</summary>
    Task<PluginInstallResult> InstallFromZipAsync(string zipFilePath, int hostAbstractionsMajor, CancellationToken cancellationToken = default);

    /// <summary>Marks an installed plugin folder for deletion at the next startup, since a currently-loaded assembly cannot be deleted while the app runs.</summary>
    Task MarkForRemovalAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>Deletes any folders marked for removal; called once at startup before discovery so a removed plugin never loads again.</summary>
    Task SweepRemovalsAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies any staged plugin updates (replacing the old folder with the new version); called once at startup before discovery, so the swap runs while no plugin assembly is loaded/locked.</summary>
    Task SweepPendingUpdatesAsync(CancellationToken cancellationToken = default);
}
