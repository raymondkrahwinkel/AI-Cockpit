using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Helm;
using Cockpit.Plugin.Kubernetes.Mcp;
using Cockpit.Plugin.Kubernetes.Security;
using Cockpit.Plugin.Kubernetes.Settings;
using Cockpit.Plugin.Kubernetes.Ui;

namespace Cockpit.Plugin.Kubernetes;

// Kubernetes plugin (AC-80): register clusters and give agents scoped, human-approved access to them through an
// mcp__cockpit-k8s__* server. The plugin talks to the kube-apiserver itself (proxy model) and keeps the credentials, so
// an agent reaches a cluster only through gated tools — opening a cluster, a namespace outside its allowed list,
// and every change all ask the operator first (see `Security.ClusterAccessGate`). This build wires the
// cluster-registration settings; the gated MCP tools are added on top of it.
public sealed class KubernetesPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "kubernetes",
        DisplayName: "Kubernetes",
        Author: "Cockpit",
        Description: "Register Kubernetes clusters and give agents scoped, human-approved access to them through an mcp__cockpit-k8s__* server. The plugin talks to the cluster itself and keeps the credentials — an agent never gets a kubeconfig. Opening a cluster asks for consent, a namespace outside the cluster's allowed list asks each session (reads included), and every change asks afresh. Cluster-scoped resources and exec/port-forward/attach are off until you turn them on per cluster. Helm releases can be read straight from their release secrets and rolled back to an earlier revision without a helm binary; an upgrade renders the chart with a cockpit-managed helm and applies that. Both approvals show the manifest diff, and there is no install or uninstall.");

    private ClusterConnectionFactory? _connections;
    private PortForwardManager? _portForwards;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new KubernetesSettings(host.Storage);
        var connections = new ClusterConnectionFactory(settings);
        _connections = connections;
        var portForwards = new PortForwardManager();
        _portForwards = portForwards;
        var gate = new ClusterAccessGate(host);
        var tools = new KubernetesMcpTools(settings, gate, connections, portForwards, new HelmRunner(), host.ResolveManagedCliPath);

        // The cockpit can install and manage the helm binary itself (AC-20/AC-1061 phase 3); helm_upgrade prefers
        // that copy over PATH via host.ResolveManagedCliPath, same as codex/claude.
        host.AddManagedCli(HelmManagedCli.Descriptor);

        host.AddSettings(() => new KubernetesSettingsControl(host, settings));
        host.AddToolbarAction(new ToolbarAction("Kubernetes settings", MaterialIconKind.Kubernetes, () => host.ShowSettingsAsync()));
        _ = host.AddMcpEndpoint("cockpit-k8s", tools, isEnabled: () => settings.McpEnabled);

        // The open tunnels appear in the status bar with an operator-only Kill (AC-82).
        host.AddSupervisedActivityProvider(portForwards);

        // A settings save may have changed a cluster's kubeconfig or context; drop the cached clients so the next
        // call rebuilds from the new config.
        host.OnSettingsSaved(connections.InvalidateAll);
    }

    public void Dispose()
    {
        // Tear the tunnels down before disposing the connections they run over — bounded so shutdown never hangs.
        try
        {
            _portForwards?.StopAllAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Best-effort teardown on shutdown; never block or throw out of Dispose.
        }

        _connections?.Dispose();
    }
}
