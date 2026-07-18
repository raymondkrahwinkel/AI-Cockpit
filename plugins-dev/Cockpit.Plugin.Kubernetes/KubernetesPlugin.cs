using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Mcp;
using Cockpit.Plugin.Kubernetes.Security;
using Cockpit.Plugin.Kubernetes.Settings;
using Cockpit.Plugin.Kubernetes.Ui;

namespace Cockpit.Plugin.Kubernetes;

/// <summary>
/// Kubernetes plugin (AC-80): register clusters and give agents scoped, human-approved access to them through an
/// mcp__k8s__* server. The plugin talks to the kube-apiserver itself (proxy model) and keeps the credentials, so
/// an agent reaches a cluster only through gated tools — opening a cluster, a namespace outside its allowed list,
/// and every change all ask the operator first (see <see cref="Security.ClusterAccessGate"/>). This build wires the
/// cluster-registration settings; the gated MCP tools are added on top of it.
/// </summary>
public sealed class KubernetesPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "kubernetes",
        DisplayName: "Kubernetes",
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "Register Kubernetes clusters and give agents scoped, human-approved access to them through an mcp__k8s__* server. The plugin talks to the cluster itself and keeps the credentials — an agent never gets a kubeconfig. Opening a cluster asks for consent, a namespace outside the cluster's allowed list asks each session (reads included), and every change asks afresh. Cluster-scoped resources and exec/port-forward/attach are off until you turn them on per cluster.");

    private ClusterConnectionFactory? _connections;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new KubernetesSettings(host.Storage);
        var connections = new ClusterConnectionFactory(settings);
        _connections = connections;
        var gate = new ClusterAccessGate(host);
        var tools = new KubernetesMcpTools(settings, gate, connections);

        host.AddSettings(() => new KubernetesSettingsControl(settings));
        _ = host.AddMcpEndpoint("k8s", tools, isEnabled: () => settings.McpEnabled);

        // A settings save may have changed a cluster's kubeconfig or context; drop the cached clients so the next
        // call rebuilds from the new config.
        host.OnSettingsSaved(connections.InvalidateAll);
    }

    public void Dispose() => _connections?.Dispose();
}
