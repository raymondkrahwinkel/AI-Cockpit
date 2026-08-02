using Cockpit.Plugins.Abstractions;
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

    private static string _KubeconfigKey(string clusterId) => $"cluster.{clusterId}.kubeconfig";
}
