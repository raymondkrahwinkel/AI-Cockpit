namespace Cockpit.Plugin.Kubernetes.Mcp;

/// <summary>
/// Classifies a resource by its <em>real</em> REST scope, not by whether the agent left the namespace blank
/// (AC-80, security review F1). The namespace jail depends on this: a namespaced kind (pods, secrets, deployments)
/// must always carry a namespace and pass the jail, and must never fall through to a cluster-wide list; only the
/// genuinely cluster-scoped kinds (nodes, namespaces, PVs, cluster roles) take the cluster-scoped path, which is
/// opt-in per cluster. Anything unknown is treated as namespaced — the safe default: an unknown cluster-scoped CRD
/// simply fails its namespaced call rather than escaping the jail.
/// </summary>
internal static class ResourceScope
{
    private static readonly HashSet<(string Group, string Plural)> ClusterScopedKinds =
    [
        ("", "namespaces"), ("", "nodes"), ("", "persistentvolumes"), ("", "componentstatuses"),
        ("rbac.authorization.k8s.io", "clusterroles"), ("rbac.authorization.k8s.io", "clusterrolebindings"),
        ("storage.k8s.io", "storageclasses"), ("storage.k8s.io", "volumeattachments"), ("storage.k8s.io", "csinodes"), ("storage.k8s.io", "csidrivers"),
        ("scheduling.k8s.io", "priorityclasses"),
        ("apiextensions.k8s.io", "customresourcedefinitions"),
        ("admissionregistration.k8s.io", "validatingwebhookconfigurations"), ("admissionregistration.k8s.io", "mutatingwebhookconfigurations"),
        ("certificates.k8s.io", "certificatesigningrequests"),
        ("apiregistration.k8s.io", "apiservices"),
        ("networking.k8s.io", "ingressclasses"),
        ("node.k8s.io", "runtimeclasses"),
        ("flowcontrol.apiserver.k8s.io", "flowschemas"), ("flowcontrol.apiserver.k8s.io", "prioritylevelconfigurations"),
    ];

    // Kinds whose contents are credential material: reading one asks for consent every time, even inside an allowed
    // namespace, because "free to read in an allowed namespace" should not silently include the crown jewels (F2).
    private static readonly HashSet<(string Group, string Plural)> SensitiveKinds =
    [
        ("", "secrets"),
    ];

    public static bool IsClusterScoped(string group, string plural) =>
        ClusterScopedKinds.Contains((group.ToLowerInvariant(), plural.ToLowerInvariant()));

    public static bool IsSensitive(string group, string plural) =>
        SensitiveKinds.Contains((group.ToLowerInvariant(), plural.ToLowerInvariant()));
}
