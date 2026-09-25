using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Kubernetes.UI;

// The UI part of Kubernetes (AC-1394): the cluster-registration settings view and the toolbar button that opens
// it. The backend part, KubernetesPlugin, keeps the MCP tools, the intent handlers, the cluster list itself
// (Settings.KubernetesSettings) and answers this part's questions over the plugin's channel.
public sealed class KubernetesUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddSettings(() => new KubernetesSettingsControl(host));
        host.AddToolbarAction(new ToolbarAction("Kubernetes settings", MaterialIconKind.Kubernetes, () => host.ShowSettingsAsync()));
    }
}
