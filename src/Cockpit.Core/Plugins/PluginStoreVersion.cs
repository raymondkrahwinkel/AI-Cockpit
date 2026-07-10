namespace Cockpit.Core.Plugins;

/// <summary>
/// One published version of a store plugin: the version string, the repo-relative <see cref="Path"/> to its
/// zip, the contract/host versions it targets, an optional <see cref="Sha256"/> of the zip for integrity
/// verification on download, and optional release notes.
/// </summary>
public sealed record PluginStoreVersion(
    string Version,
    string Path,
    int? AbstractionsVersion,
    string? MinHostVersion,
    string? Sha256,
    string? Notes);
