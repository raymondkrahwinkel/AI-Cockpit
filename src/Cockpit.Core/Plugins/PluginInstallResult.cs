namespace Cockpit.Core.Plugins;

// AC-1013 (#14): Outcome of installing a plugin .zip — folder id and entry-assembly SHA-256 to pin on
// success (staged to `.pending-updates`, live after restart, for updates), or a rejection reason.
// (Omitted: no-exceptions rationale and why an update never re-prompts consent; see ticket.)
public sealed record PluginInstallResult(bool IsSuccess, string? Error, string? FolderId, string? Sha256 = null, bool Staged = false)
{
    public static PluginInstallResult Success(string folderId, string? sha256 = null, bool staged = false) => new(true, null, folderId, sha256, staged);

    public static PluginInstallResult Failure(string error) => new(false, error, null);
}
