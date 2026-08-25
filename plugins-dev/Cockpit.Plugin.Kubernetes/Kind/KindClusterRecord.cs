namespace Cockpit.Plugin.Kubernetes.Kind;

// One kind cluster the plugin created (AC-179): the registry, not the containers on disk, is the source of truth
// for cleanup, mirroring `WorktreeRecord`. `IsPinned` is the operator-set (never agent-set) exception that keeps a
// cluster alive across both the orphan sweep and the TTL backstop.
internal sealed record KindClusterRecord(
    string Name,
    string OwnerPaneId,
    string KubeconfigPath,
    DateTimeOffset CreatedAt,
    bool IsPinned = false);
