namespace Cockpit.Core.Plugins;

/// <summary>
/// Outcome of installing a plugin from a <c>.zip</c> (#14): the folder id the plugin landed under on
/// success (and the SHA-256 of the newly installed entry assembly — which for an update is staged to
/// <c>.pending-updates</c> and only becomes live after the next restart, so it is the hash to pin so the
/// updated plugin stays enabled), or a human-readable reason it was rejected. No exceptions for the expected
/// rejection cases.
/// </summary>
/// <param name="Staged">
/// True when this was an update over an existing install — the new bytes were staged to
/// <c>.pending-updates</c> and only go live on the next restart. An update never re-prompts consent (the
/// operator explicitly updated a plugin they already have); a fresh install (<c>Staged</c> false) does.
/// </param>
public sealed record PluginInstallResult(bool IsSuccess, string? Error, string? FolderId, string? Sha256 = null, bool Staged = false)
{
    public static PluginInstallResult Success(string folderId, string? sha256 = null, bool staged = false) => new(true, null, folderId, sha256, staged);

    public static PluginInstallResult Failure(string error) => new(false, error, null);
}
