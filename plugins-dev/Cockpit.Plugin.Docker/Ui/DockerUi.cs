using Material.Icons;
using Cockpit.Plugin.Docker.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Docker.UI;

// The UI part of Docker (AC-1394): the settings view and the toolbar action. The backend part, DockerPlugin, keeps
// the daemon engine, the MCP endpoint and the status bar registration.
public sealed class DockerUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new DockerSettings(host.Storage);
        host.AddSettings(() => new DockerSettingsControl(settings));
        host.AddToolbarAction(new ToolbarAction("Docker settings", MaterialIconKind.Docker, () => host.ShowSettingsAsync()));
    }
}
