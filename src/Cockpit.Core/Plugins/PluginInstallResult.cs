namespace Cockpit.Core.Plugins;

/// <summary>
/// Outcome of installing a plugin from a <c>.zip</c> (#14): the folder id the plugin landed under on
/// success, or a human-readable reason it was rejected (bad archive, unsafe path, missing/invalid
/// manifest, incompatible abstractions major). No exceptions for the expected rejection cases.
/// </summary>
public sealed record PluginInstallResult(bool IsSuccess, string? Error, string? FolderId)
{
    public static PluginInstallResult Success(string folderId) => new(true, null, folderId);

    public static PluginInstallResult Failure(string error) => new(false, error, null);
}
