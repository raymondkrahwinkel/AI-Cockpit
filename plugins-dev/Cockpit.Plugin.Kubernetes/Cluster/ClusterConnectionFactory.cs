using System.Collections.Concurrent;
using System.Text;
using k8s;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Cluster;

/// <summary>
/// Builds and caches the <see cref="IKubernetes"/> client for a registered cluster from its stored kubeconfig and
/// chosen context (AC-80). The plugin holds the credentials here and hands the client only to the gated tools —
/// nothing else in the process, and never an agent, sees the kubeconfig. One client per cluster, kept for reuse; a
/// settings change to a cluster invalidates its cached client so the next call rebuilds from the new config.
/// </summary>
internal sealed class ClusterConnectionFactory(KubernetesSettings settings) : IDisposable
{
    private readonly ConcurrentDictionary<string, IKubernetes> _clients = new(StringComparer.Ordinal);

    /// <summary>
    /// A connected client for the cluster, or an error string when no kubeconfig is stored or the config will not
    /// build. Building does not itself reach the cluster (an exec-auth context runs its plugin on the first call,
    /// not here), so a returned client is ready but unproven until it is used.
    /// </summary>
    public (IKubernetes? Client, string? Error) Connect(ClusterRegistration cluster)
    {
        if (_clients.TryGetValue(cluster.Id, out var cached))
        {
            return (cached, null);
        }

        var (client, error) = _BuildClient(cluster);
        if (client is null)
        {
            return (null, error);
        }

        // Two calls racing on the same not-yet-cached cluster both build a client (each owning an HttpClient);
        // GetOrAdd keeps one, so dispose the loser instead of leaking it.
        var winner = _clients.GetOrAdd(cluster.Id, client);
        if (!ReferenceEquals(winner, client))
        {
            client.Dispose();
        }

        return (winner, null);
    }

    /// <summary>
    /// A fresh client the caller owns and must dispose — deliberately NOT cached. A long-lived holder (a port-forward
    /// tunnel) needs a client that <see cref="InvalidateAll"/> cannot dispose out from under it on a settings save.
    /// </summary>
    public (IKubernetes? Client, string? Error) ConnectDedicated(ClusterRegistration cluster) => _BuildClient(cluster);

    private (IKubernetes? Client, string? Error) _BuildClient(ClusterRegistration cluster)
    {
        var context = string.IsNullOrWhiteSpace(cluster.ContextName) ? null : cluster.ContextName;
        try
        {
            KubernetesClientConfiguration config;
            if (!string.IsNullOrWhiteSpace(cluster.KubeconfigPath))
            {
                // Path model: build from the file on disk, letting the client resolve relative cert paths against the
                // file's own directory. An exec-auth context re-runs its credential plugin on each call, so its token
                // refreshes stay live; a rotated *static* token in the file is only picked up when the cached client is
                // rebuilt (after a settings save invalidates it).
                var path = KubeconfigInspector.ExpandPath(cluster.KubeconfigPath);
                if (!File.Exists(path))
                {
                    // Label only — the absolute path is host detail the agent should not see (it names the user/home).
                    return (null, $"The kubeconfig for cluster \"{cluster.Label}\" is not available. Check its file path in the Kubernetes plugin settings.");
                }

                config = KubernetesClientConfiguration.BuildConfigFromConfigFile(new FileInfo(path), currentContext: context);
            }
            else
            {
                var kubeconfig = settings.GetKubeconfig(cluster.Id);
                if (kubeconfig is null)
                {
                    return (null, $"No kubeconfig is set for cluster \"{cluster.Label}\". Add a file path or paste one in the Kubernetes plugin settings.");
                }

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kubeconfig));
                config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream, currentContext: context);
            }

            return (new k8s.Kubernetes(config), null);
        }
        catch (Exception)
        {
            // Label only — a build/parse failure message can echo the file path or its contents to the agent.
            return (null, $"Could not connect to cluster \"{cluster.Label}\". Check its kubeconfig in the Kubernetes plugin settings.");
        }
    }

    /// <summary>Drops a cluster's cached client (e.g. after its settings changed), so the next call rebuilds it.</summary>
    public void Invalidate(string clusterId)
    {
        if (_clients.TryRemove(clusterId, out var client))
        {
            client.Dispose();
        }
    }

    /// <summary>Drops every cached client — used after a settings save, since a cluster's kubeconfig or context may have changed.</summary>
    public void InvalidateAll()
    {
        foreach (var clusterId in _clients.Keys.ToArray())
        {
            Invalidate(clusterId);
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
    }
}
