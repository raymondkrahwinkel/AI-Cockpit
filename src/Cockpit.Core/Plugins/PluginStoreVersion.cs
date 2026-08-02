namespace Cockpit.Core.Plugins;

// One published version of a store plugin: the version string, the repo-relative `Path` to its
// zip, the contract/host versions it targets, an optional `Sha256` of the zip for integrity
// verification on download, and optional release notes.
public sealed record PluginStoreVersion(
    string Version,
    string Path,
    int? AbstractionsVersion,
    string? MinHostVersion,
    string? Sha256,
    string? Notes);
