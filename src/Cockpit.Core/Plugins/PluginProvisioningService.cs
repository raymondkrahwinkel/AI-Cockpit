using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;

namespace Cockpit.Core.Plugins;

/// <summary>
/// Implements the provisioning seam (AC-510[b]) over <see cref="IPluginStoreClient"/> and
/// <see cref="IPluginInstaller"/> — the download-verify-install glue that used to live only inside
/// <c>PluginManagerViewModel</c>. A fresh install's consent walk and a staged update's registration re-pin stay
/// with the caller: both need UI (a dialog) or session state this service has no business holding, so this class
/// stops at "here is what landed", which is exactly what a screen-less caller needs too.
/// </summary>
public sealed class PluginProvisioningService(IPluginStoreClient storeClient, IPluginInstaller installer)
    : IPluginProvisioningService, ISingletonService
{
    public async Task<PluginProvisionResult> InstallAsync(
        PluginProvisionRequest request, int hostAbstractionsMajor, Version? hostVersion = null, CancellationToken cancellationToken = default)
    {
        var effectiveHostVersion = hostVersion ?? HostVersionInfo.Current;

        // Checked before anything is fetched (AC-181): a version this host cannot run is refused without spending
        // a download on it, and the installer's own gate (a defense-in-depth second check) never gets the chance
        // to be the only thing that caught it.
        if (!PluginCompatibility.IsCompatible(request.Version, hostAbstractionsMajor, effectiveHostVersion))
        {
            var reason = PluginCompatibility.IncompatibilityReason(request.Version, hostAbstractionsMajor, effectiveHostVersion);
            return new PluginProvisionResult(PluginProvisionOutcome.Incompatible, request.Id, request.Name, reason, Warning: null, FolderId: null, Sha256: null);
        }

        var download = await storeClient.DownloadZipAsync(request.Store, request.Version.Path, request.Version.Sha256, cancellationToken).ConfigureAwait(false);
        if (!download.IsSuccess || download.ZipPath is null)
        {
            return new PluginProvisionResult(PluginProvisionOutcome.Failed, request.Id, request.Name, download.Error ?? "Download failed.", Warning: null, FolderId: null, Sha256: null);
        }

        try
        {
            var installed = await installer.InstallFromZipAsync(download.ZipPath, hostAbstractionsMajor, effectiveHostVersion, cancellationToken).ConfigureAwait(false);
            if (!installed.IsSuccess)
            {
                return new PluginProvisionResult(PluginProvisionOutcome.Failed, request.Id, request.Name, installed.Error ?? "Install failed.", Warning: null, FolderId: null, Sha256: null);
            }

            var outcome = installed.Staged ? PluginProvisionOutcome.Staged : PluginProvisionOutcome.Installed;
            return new PluginProvisionResult(outcome, request.Id, request.Name, Error: null, download.Warning, installed.FolderId, installed.Sha256);
        }
        finally
        {
            _TryDelete(download.ZipPath);
        }
    }

    public async Task<PluginProvisionBatchResult> InstallManyAsync(
        IReadOnlyList<PluginProvisionRequest> requests, int hostAbstractionsMajor, Version? hostVersion = null, CancellationToken cancellationToken = default)
    {
        var results = new List<PluginProvisionResult>(requests.Count);
        foreach (var request in requests)
        {
            try
            {
                results.Add(await InstallAsync(request, hostAbstractionsMajor, hostVersion, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                // Isolate: one plugin throwing (a network fault mid-download, same as PluginManagerViewModel's
                // batch already tolerates) must not take the rest of the batch down with it.
                results.Add(new PluginProvisionResult(PluginProvisionOutcome.Failed, request.Id, request.Name, exception.Message, Warning: null, FolderId: null, Sha256: null));
            }
        }

        return new PluginProvisionBatchResult(results);
    }

    private static void _TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A leftover temp download is harmless; the OS temp cleaner reclaims it.
        }
    }
}
