using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Installs and removes plugins on disk (#14). Installation validates and safely unpacks a <c>.zip</c> into the
/// plugins root; an update over a loaded plugin and a removal are both deferred to the next startup, since a
/// loaded assembly's file stays locked (on Windows) until the process exits. Enable/consent state lives in <see cref="IPluginRegistrationStore"/>.
/// </summary>
public interface IPluginInstaller
{
    /// <summary>
    /// Validates and unpacks the archive; returns the folder id on success or a reason it was rejected — including
    /// a <c>minHostVersion</c> newer than this cockpit (AC-181), checked here rather than only at load. Updating
    /// an existing install stages the new version for <see cref="SweepPendingUpdatesAsync"/>; <paramref name="hostVersion"/> defaults to <see cref="HostVersionInfo"/>, only overridden explicitly in a test.
    /// </summary>
    Task<PluginInstallResult> InstallFromZipAsync(
        string zipFilePath, int hostAbstractionsMajor, Version? hostVersion = null, CancellationToken cancellationToken = default);

    /// <summary>Marks an installed plugin folder for deletion at the next startup, since a currently-loaded assembly cannot be deleted while the app runs.</summary>
    Task MarkForRemovalAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>Deletes any folders marked for removal; called once at startup before discovery so a removed plugin never loads again.</summary>
    Task SweepRemovalsAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies any staged plugin updates (replacing the old folder with the new version); called once at startup before discovery, so the swap runs while no plugin assembly is loaded/locked.</summary>
    Task SweepPendingUpdatesAsync(CancellationToken cancellationToken = default);
}
