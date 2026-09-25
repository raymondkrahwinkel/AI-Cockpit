using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

// The UI part of Depot (AC-1394): the settings view and the global-toolbar shortcut to it. The backend part,
// DepotPlugin, keeps the project-memory-source/shared-project-source/MCP-provider registrations and syncs them
// whenever this part reports a connection-list save over the plugin's channel.
public sealed class DepotUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new DepotSettings(host.Storage);
        host.AddSettings(() => new DepotSettingsControl(host, settings));
        // AC-784: same global-toolbar route the Kubernetes plugin uses (AC-91) — the project editor's own
        // "Servers…" button above still opens settings the same way it always has.
        host.AddToolbarAction(new ToolbarAction("Depot settings", MaterialIconKind.Database, () => host.ShowSettingsAsync()));
    }
}
