namespace Cockpit.Core.Plugins;

// One plugin found on disk during discovery: its folder, the folder id (the normalized-id-or-GUID that keys
// its registration), the parsed manifest, the closure hash over every file in that folder (AC-43, not the
// entry assembly alone) and the load decision. The loader acts on the ones that decided `PluginLoadDecision.Load`.
public sealed record DiscoveredPlugin(
    string FolderPath,
    string FolderId,
    PluginManifest Manifest,
    string Sha256,
    PluginLoadDecision Decision);
