using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Contracts;
using Cockpit.Plugin.Kubernetes.Helm;
using Cockpit.Plugin.Kubernetes.Mcp;
using Cockpit.Plugin.Kubernetes.Security;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes;

// Kubernetes plugin (AC-80): register clusters and give agents scoped, human-approved access to them through an
// mcp__cockpit-k8s__* server. The plugin talks to the kube-apiserver itself (proxy model) and keeps the credentials, so
// an agent reaches a cluster only through gated tools — opening a cluster, a namespace outside its allowed list,
// and every change all ask the operator first (see `Security.ClusterAccessGate`). This build wires the
// cluster-registration settings; the gated MCP tools are added on top of it.
//
// AC-1394: the backend part. The settings view and the toolbar button that opens it live in UI/KubernetesUi. The
// cluster list itself crosses the channel as Contracts records (see Settings.ClusterSettingsChannel) rather than
// ClusterRegistration/KubernetesSettings directly, and kubeconfig parsing (context listing, exec-auth detection)
// stays here too — both need the KubernetesClient package, which the UI part deliberately does not reference.
public sealed class KubernetesPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "kubernetes",
        DisplayName: "Kubernetes",
        Author: "Cockpit",
        Description: "Register Kubernetes clusters and give agents scoped, human-approved access to them through an mcp__cockpit-k8s__* server. The plugin talks to the cluster itself and keeps the credentials — an agent never gets a kubeconfig. Opening a cluster asks for consent, a namespace outside the cluster's allowed list asks each session (reads included), and every change asks afresh. Cluster-scoped resources and exec/port-forward/attach are off until you turn them on per cluster. Helm releases can be read straight from their release secrets and rolled back to an earlier revision without a helm binary; an upgrade renders the chart with a cockpit-managed helm and applies that. Both approvals show the manifest diff, and there is no install or uninstall.");

    private readonly List<IDisposable> _handlers = [];
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

        // AC-1083: the Kind plugin registers the cluster it just created here, addressed by manifest id.
        var registrations = new ClusterRegistrationIntents(settings);
        host.RegisterIntentHandler(ClusterRegistrationIntents.RegisterAction, registrations.RegisterAsync);
        host.RegisterIntentHandler(ClusterRegistrationIntents.UnregisterAction, registrations.UnregisterAsync);

        _ = host.AddMcpEndpoint("cockpit-k8s", tools, isEnabled: () => settings.McpEnabled);

        // The open tunnels appear in the status bar with an operator-only Kill (AC-82).
        host.AddSupervisedActivityProvider(portForwards);

        // A settings save may have changed a cluster's kubeconfig or context; drop the cached clients so the next
        // call rebuilds from the new config.
        host.OnSettingsSaved(connections.InvalidateAll);

        // What the settings view (UI/ClusterRowControl, UI/KubernetesSettingsControl) asks over the channel — it
        // never references ClusterRegistration/KubernetesSettings or the KubernetesClient package directly.
        var clusterSettingsChannel = new ClusterSettingsChannel(settings);
        _handlers.Add(host.Channel.Handle(KubernetesChannel.KubeconfigContexts, (payload, cancellationToken) => _ContextsAsync(payload)));
        _handlers.Add(host.Channel.Handle(KubernetesChannel.LoadClusterSettings, (payload, cancellationToken) => clusterSettingsChannel.LoadAsync()));
        _handlers.Add(host.Channel.Handle(KubernetesChannel.SaveClusterSettings, (payload, cancellationToken) => clusterSettingsChannel.SaveAsync(payload)));
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _handlers.Clear();

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

    private static Task<JsonElement> _ContextsAsync(JsonElement payload)
    {
        var request = payload.Deserialize<KubeconfigContextsRequest>(KubernetesChannel.Json)
            ?? throw new ArgumentException("The request names no kubeconfig source.", nameof(payload));
        var yaml = KubeconfigInspector.ReadYaml(request.KubeconfigPath, request.PastedKubeconfig);
        KubeconfigContextsAnswer? answer = yaml is null ? null : new KubeconfigContextsAnswer(KubeconfigInspector.ListContexts(yaml).Names);
        return Task.FromResult(JsonSerializer.SerializeToElement(answer, KubernetesChannel.Json));
    }
}
