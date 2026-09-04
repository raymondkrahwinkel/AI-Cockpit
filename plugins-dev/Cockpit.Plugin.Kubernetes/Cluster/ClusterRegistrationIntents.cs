using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Cluster;

// Lets another plugin that manages a cluster's lifecycle register it here (AC-1083), so its kubeconfig reaches the
// k8s tools without the operator retyping it — the Kind plugin is the first caller. Addressed by manifest id and an
// agreed action string (AC-95), so nothing here knows what kind is or references its types.
internal sealed class ClusterRegistrationIntents(KubernetesSettings settings)
{
    public const string RegisterAction = "cluster.register";
    public const string UnregisterAction = "cluster.unregister";

    public Task<IReadOnlyDictionary<string, string>> RegisterAsync(PluginIntent intent)
    {
        var id = intent.Data.GetValueOrDefault("id", string.Empty);
        var kubeconfigPath = intent.Data.GetValueOrDefault("kubeconfigPath", string.Empty);
        if (id.Length == 0 || kubeconfigPath.Length == 0)
        {
            return _Notice("The cluster was not registered: the request carried no id or no kubeconfig path.");
        }

        if (settings.FindCluster(id) is not null)
        {
            // A registration the operator made or edited by hand is never silently overwritten.
            return _Notice($"A cluster registration named \"{id}\" already existed and was left unchanged — check it points at this kubeconfig.");
        }

        // The narrowest jail an intent-registered cluster can have; widen it in the plugin settings if needed.
        settings.Clusters = [.. settings.Clusters, new ClusterRegistration(
            id,
            intent.Data.GetValueOrDefault("label", id),
            intent.Data.GetValueOrDefault("context", string.Empty),
            ["default"],
            KubeconfigPath: kubeconfigPath)];
        return _Notice(null);
    }

    public Task<IReadOnlyDictionary<string, string>> UnregisterAsync(PluginIntent intent)
    {
        var id = intent.Data.GetValueOrDefault("id", string.Empty);
        settings.Clusters = settings.Clusters.Where(cluster => !string.Equals(cluster.Id, id, StringComparison.Ordinal)).ToList();
        settings.ClearKubeconfig(id);
        return _Notice(null);
    }

    private static Task<IReadOnlyDictionary<string, string>> _Notice(string? notice) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["notice"] = notice ?? string.Empty });
}
