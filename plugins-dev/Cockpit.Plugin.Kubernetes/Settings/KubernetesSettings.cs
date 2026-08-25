using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kubernetes.Kind;
using Cockpit.Plugin.Kubernetes.Model;

namespace Cockpit.Plugin.Kubernetes.Settings;

// The plugin's settings, persisted through the host's per-plugin `IPluginStorage` (AC-80). The cluster
// list is non-secret metadata (`ClusterRegistration`) stored as JSON; each cluster's kubeconfig is a
// credential and goes through the secret layer under its own key, never into the metadata. Read fresh on every
// access, so a change made in the settings view takes effect on the next call without a restart.
internal sealed class KubernetesSettings(IPluginStorage storage)
{
    public IReadOnlyList<ClusterRegistration> Clusters
    {
        get => storage.Get<List<ClusterRegistration>>("clusters") ?? [];
        set => storage.Set("clusters", value.ToList());
    }

    // Whether the k8s MCP server is offered to sessions. On by default until the operator turns it off.
    public bool McpEnabled
    {
        get => storage.Get<bool?>("mcpEnabled") ?? true;
        set => storage.Set("mcpEnabled", value);
    }

    public ClusterRegistration? FindCluster(string clusterId) =>
        Clusters.FirstOrDefault(cluster => string.Equals(cluster.Id, clusterId, StringComparison.Ordinal));

    // The kubeconfig stored for a cluster, or null when none is set. Written through the secret layer, so it is encrypted at rest when the operator has that on.
    public string? GetKubeconfig(string clusterId) =>
        storage.GetSecret(_KubeconfigKey(clusterId)) is { Length: > 0 } content ? content : null;

    public void SetKubeconfig(string clusterId, string content) =>
        storage.SetSecret(_KubeconfigKey(clusterId), content);

    // Clears a cluster's stored kubeconfig — used when the operator removes the cluster, so its credential does not linger.
    public void ClearKubeconfig(string clusterId) =>
        storage.SetSecret(_KubeconfigKey(clusterId), string.Empty);

    // AC-576 phase 3: a read-only Argo CD project-role token, stored the same way as the kubeconfig — through
    // the secret layer, keyed to the cluster. Null (no token set) means the Argo API tools are unavailable.
    public string? GetArgoToken(string clusterId) =>
        storage.GetSecret(_ArgoTokenKey(clusterId)) is { Length: > 0 } content ? content : null;

    public void SetArgoToken(string clusterId, string token) =>
        storage.SetSecret(_ArgoTokenKey(clusterId), token);

    public void ClearArgoToken(string clusterId) =>
        storage.SetSecret(_ArgoTokenKey(clusterId), string.Empty);

    private static string _KubeconfigKey(string clusterId) => $"cluster.{clusterId}.kubeconfig";
    private static string _ArgoTokenKey(string clusterId) => $"cluster.{clusterId}.argoToken";

    // AC-179: the kind-cluster registry, same non-secret list idiom as `Clusters` — a kind cluster's kubeconfig is
    // a plain file path on disk, not a pasted secret, so it lives in the record itself rather than the secret layer.
    public IReadOnlyList<KindClusterRecord> KindClusters
    {
        get => storage.Get<List<KindClusterRecord>>("kindClusters") ?? [];
        set => storage.Set("kindClusters", value.ToList());
    }

    // The TTL backstop (AC-179 criterion 11), next to criterion 8's live-session sweep rather than instead of it.
    // Four hours covers a normal working session without leaving a forgotten cluster's 632 MiB idle overnight.
    public TimeSpan KindClusterMaxLifetime
    {
        get => TimeSpan.FromHours(storage.Get<double?>("kindClusterMaxLifetimeHours") ?? 4.0);
        set => storage.Set("kindClusterMaxLifetimeHours", value.TotalHours);
    }
}
