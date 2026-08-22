using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// The provisioning seam (AC-510[b]): fetch a store version → verify its checksum → install it → report which of
/// the four outcomes it landed on (<see cref="PluginProvisionOutcome"/>), without any UI of its own. Wraps
/// <see cref="IPluginStoreClient"/> and <see cref="IPluginInstaller"/> — both already UI-free — so this is the
/// one place their glue lives; the store dialog's install/update/rollback commands and any future installer that
/// never shows a store screen at all (AC-541) both call the same instance.
/// </summary>
public interface IPluginProvisioningService
{
    /// <summary>
    /// Installs one plugin. Refuses before any download when this host cannot run the requested version (AC-181);
    /// otherwise downloads (the store's published checksum still verified — a mismatch is a hard rejection, a
    /// missing one a warning carried on a success) and installs it, staging over an existing install.
    /// </summary>
    /// <param name="hostAbstractionsMajor">
    /// The running cockpit's plugin-contract major — required, not defaulted: this project does not reference the
    /// plugin-abstractions assembly that owns the constant, so a caller (which does) always passes it explicitly.
    /// </param>
    Task<PluginProvisionResult> InstallAsync(
        PluginProvisionRequest request, int hostAbstractionsMajor, Version? hostVersion = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs several plugins, one plugin failing isolated from the rest so the batch runs to completion — the
    /// same pattern a UI batch-update already follows, available here without one.
    /// </summary>
    Task<PluginProvisionBatchResult> InstallManyAsync(
        IReadOnlyList<PluginProvisionRequest> requests, int hostAbstractionsMajor, Version? hostVersion = null, CancellationToken cancellationToken = default);
}
