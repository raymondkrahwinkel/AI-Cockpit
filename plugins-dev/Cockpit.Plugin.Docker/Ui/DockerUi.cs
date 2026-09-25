using System.Text.Json;
using Material.Icons;
using Cockpit.Plugin.Docker.Contracts;
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
        host.AddSettings(() => new DockerSettingsControl(host, settings));
        host.AddToolbarAction(new ToolbarAction("Docker settings", MaterialIconKind.Docker, () => host.ShowSettingsAsync()));

        // A saved endpoint may have moved; tell the backend part to drop its cached daemon client (DockerEngine)
        // so the next call rebuilds against it. ponytail: OnSettingsSaved's callback is a synchronous Action, so
        // it cannot await; blocking is safe today (the channel answers in-process with no thread hop).
        host.OnSettingsSaved(() =>
            host.Channel.InvokeAsync(DockerChannel.SettingsSaved, JsonSerializer.SerializeToElement(true, DockerChannel.Json)).GetAwaiter().GetResult());
    }
}
