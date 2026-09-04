namespace Cockpit.Plugin.Kind;

// The kind_list MCP tool's per-cluster row (AC-179 criterion 3) — everything KindClusterRecord carries plus the
// derived Age and the live IsRunning check against `kind get clusters`, which the registry itself never tracks.
internal sealed record KindClusterListEntry(
    string Name,
    TimeSpan Age,
    string OwnerPaneId,
    string KubeconfigPath,
    bool IsPinned,
    bool IsRunning);
