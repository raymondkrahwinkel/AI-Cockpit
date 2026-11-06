namespace Cockpit.Core.Plugins;

// AC-1013 (#14): Outcome of installing a plugin .zip — folder id and entry-assembly SHA-256 to pin on
// success (staged to `.pending-updates`, live after restart, for updates), or a rejection reason.
// (Omitted: no-exceptions rationale and why an update never re-prompts consent; see ticket.)
public sealed record PluginInstallResult(
    bool IsSuccess,
    string? Error,
    string? FolderId,
    string? Sha256 = null,
    bool Staged = false,
    PluginManifest? Manifest = null,
    bool InPlace = false,
    PluginLiveState? Live = null,
    string? ConsentReason = null,
    string? ReplacedSha256 = null)
{
    // AC-2025: `consentReason` says an update asks for more than the approved version had, so it waits for the restart and a new approval.
    // `replacedSha256` is the hash of the folder an update replaced, which a caller compares with the approved pin.
    // `inPlace` says the update replaced `plugins/<id>` already, so a live swap can load it; false waits in `.pending-updates`.
    public static PluginInstallResult Success(string folderId, string? sha256 = null, bool staged = false, PluginManifest? manifest = null, bool inPlace = false, string? consentReason = null, string? replacedSha256 = null) =>
        new(true, null, folderId, sha256, staged, manifest, inPlace, ConsentReason: consentReason, ReplacedSha256: replacedSha256);

    public static PluginInstallResult Failure(string error) => new(false, error, null);
}
