using Material.Icons;
using Cockpit.Plugin.Kind.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Kind.UI;

// The UI part of Kind (AC-1394): the settings view and the toolbar action. The backend part, KindPlugin, keeps the
// cluster registry, the MCP endpoint and the status bar registration.
public sealed class KindUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new KindSettings(host.Storage);
        host.AddSettings(() => new KindSettingsControl(host, settings));
        host.AddToolbarAction(new ToolbarAction("Kind settings", MaterialIconKind.Kubernetes, () => host.ShowSettingsAsync()));
    }
}
