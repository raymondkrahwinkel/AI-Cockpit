namespace Cockpit.Core.Plugins;

/// <summary>One plugin advertised by a store: its identity, display fields, the latest version and the full version history.</summary>
public sealed record PluginStoreEntry(
    string Id,
    string Name,
    string? Description,
    string? Author,
    string LatestVersion,
    IReadOnlyList<PluginStoreVersion> Versions);
