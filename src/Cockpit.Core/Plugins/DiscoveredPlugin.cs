namespace Cockpit.Core.Plugins;

/// <summary>
/// One plugin found on disk during discovery: its folder, the folder id (the normalized-id-or-GUID that
/// keys its registration), the parsed manifest, the current entry-assembly hash and the resulting load
/// decision. The plugin manager renders these; the loader acts on the ones that decided <see cref="PluginLoadDecision.Load"/>.
/// </summary>
public sealed record DiscoveredPlugin(
    string FolderPath,
    string FolderId,
    PluginManifest Manifest,
    string Sha256,
    PluginLoadDecision Decision);
