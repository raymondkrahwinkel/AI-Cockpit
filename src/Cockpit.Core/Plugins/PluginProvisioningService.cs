using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;

namespace Cockpit.Core.Plugins;

// AC-510[b]: Provisioning seam over IPluginStoreClient/IPluginInstaller — download-verify-install glue
// moved out of PluginManagerViewModel. Consent walk and registration re-pin stay with the caller, since
// both need UI/session state this service shouldn't hold. (Omitted: screen-less-caller rationale; see ticket.)
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
