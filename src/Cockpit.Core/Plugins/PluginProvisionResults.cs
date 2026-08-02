namespace Cockpit.Core.Plugins;

// What happened when `PluginProvisioningService` tried to install one plugin (AC-510[b]). The default
// value is deliberately the safe/negative one: an unconfigured result (a fake that never sets it, a forgotten
// branch) reads as failed, never as a silent success.
public enum PluginProvisionOutcome
{
    // The download or the install step refused it — a store the request could not reach, a checksum
    // mismatch, a corrupt archive, or the installer's own gate. `PluginProvisionResult.Error` says which.
    Failed = 0,

    // Refused before any bytes were fetched — this host does not meet the version's contract-major or
    // `minHostVersion` (AC-181). Nothing was downloaded.
    Incompatible,

    // A fresh install landed under the plugins root. Whether it still needs the operator's consent is the
    // caller's concern, not this service's.
    Installed,

    // An update over an existing install — staged under `.pending-updates` and live only after the
    // next restart.
    Staged,
}

// One plugin to install: the identity the caller reports it by, the store to fetch it from, and the
// specific version (its zip path, checksum, and compatibility fields).
public sealed record PluginProvisionRequest(string Id, string Name, PluginStoreConfig Store, PluginStoreVersion Version);

// The outcome of one `PluginProvisioningService.InstallAsync` call: which of the four forms it took
// (`PluginProvisionOutcome`), the folder id and entry-assembly hash a successful install landed under,
// and a non-fatal warning (an unverified/missing checksum) carried alongside a success.
public sealed record PluginProvisionResult(
    PluginProvisionOutcome Outcome,
    string Id,
    string Name,
    string? Error,
    string? Warning,
    string? FolderId,
    string? Sha256)
{
    public bool IsSuccess => Outcome is PluginProvisionOutcome.Installed or PluginProvisionOutcome.Staged;
}

// The result of installing several plugins in one batch (AC-510[b]): one plugin failing must not abort the rest —
// the same isolate-and-continue pattern `PluginManagerViewModel.UpdateAllAsync` already uses — so the caller
// gets a per-plugin result plus the summary of what did and did not land.
public sealed record PluginProvisionBatchResult(IReadOnlyList<PluginProvisionResult> Results)
{
    public int SucceededCount => Results.Count(result => result.IsSuccess);

    public IReadOnlyList<string> FailedNames => Results.Where(result => !result.IsSuccess).Select(result => result.Name).ToList();
}
