using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Installs and removes plugins on disk (#14). Installation validates and safely unpacks a <c>.zip</c>
/// into the plugins root; removal marks a folder so it is deleted at the next startup (a loaded plugin's
/// assembly is locked until then). The enable/consent state itself lives in <see cref="IPluginRegistrationStore"/>.
/// </summary>
public interface IPluginInstaller
{
    /// <summary>Validates and unpacks the archive into its own folder under the plugins root; returns the folder id on success or a reason it was rejected.</summary>
    Task<PluginInstallResult> InstallFromZipAsync(string zipFilePath, int hostAbstractionsMajor, CancellationToken cancellationToken = default);

    /// <summary>Marks an installed plugin folder for deletion at the next startup, since a currently-loaded assembly cannot be deleted while the app runs.</summary>
    Task MarkForRemovalAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>Deletes any folders marked for removal; called once at startup before discovery so a removed plugin never loads again.</summary>
    Task SweepRemovalsAsync(CancellationToken cancellationToken = default);
}
