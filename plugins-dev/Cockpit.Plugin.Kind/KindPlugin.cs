using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Kind.Cli;
using Cockpit.Plugin.Kind.Mcp;
using Cockpit.Plugin.Kind.Security;
using Cockpit.Plugin.Kind.Settings;
using Cockpit.Plugin.Kind.Ui;

namespace Cockpit.Plugin.Kind;

// Kind plugin (AC-1079, split out of the Kubernetes plugin): disposable local kind (Kubernetes-in-Docker) clusters
// through an mcp__cockpit-kind__* server. Create and delete each ask the operator afresh (see
// `Security.KindConsentGate`); a non-pinned cluster is torn down on session close, cockpit exit, or its TTL.
public sealed class KindPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "kind",
        DisplayName: "Kind",
        Author: "Cockpit",
        Description: "Disposable local kind (Kubernetes-in-Docker) clusters through an mcp__cockpit-kind__* server. kind_create spins one up, kind_list shows what this plugin made and kind_delete tears one down — never a cluster made outside the cockpit. Create and delete each ask the operator afresh, showing the literal kind command, and an approval is never remembered. A non-pinned cluster is torn down when its owning session closes, when the cockpit exits, or when its configurable lifetime runs out. Needs the kind binary and a container runtime (Docker or Podman) already on the machine.");

    private KindClusterManager? _clusters;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new KindSettings(host.Storage);

        // No ManagedCli for kind (AC-179, deliberate): the heavy half of the chain — a container runtime plus a
        // 1.3+ GB node image — is not something the cockpit can manage either, so a managed binary would not
        // deliver "it just works". PATH-probe only, mirroring ActRuntimeStatus's "say what to install" approach.
        var runner = new CliRunner();
        var kubeconfigDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cockpit", "kubernetes-kind");
        var clusters = new KindClusterManager(settings, runner, new KindRuntime(runner), "kind", kubeconfigDirectory);
        _clusters = clusters;

        host.AddSettings(() => new KindSettingsControl(host, settings));
        host.AddToolbarAction(new ToolbarAction("Kind settings", MaterialIconKind.Kubernetes, () => host.ShowSettingsAsync()));
        _ = host.AddMcpEndpoint("cockpit-kind", new KindMcpTools(new KindConsentGate(host), clusters, host), isEnabled: () => settings.McpEnabled);

        // The running clusters appear in the status bar with an operator-only Kill (AC-179).
        host.AddSupervisedActivityProvider(clusters);

        // AC-179 criterion 8: a kind cluster whose owning pane is not among this start's live sessions is orphaned.
        // Fire-and-forget: this must not delay plugin startup.
        _ = clusters.ReconcileAsync(host.Sessions.OpenSessions.Select(session => session.PaneId).ToList(), CancellationToken.None);
    }

    public void Dispose()
    {
        // AC-179 criterion 9 (D2): non-pinned kind clusters are disposable test environments, torn down on close.
        try
        {
            _clusters?.StopAllAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Best-effort teardown on shutdown; never block or throw out of Dispose.
        }
    }
}
