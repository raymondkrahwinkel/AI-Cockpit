using System.Text.Json;
using Material.Icons;
using Cockpit.Plugin.Proxmox.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Proxmox.UI;

// The UI part of Proxmox (AC-1394): the settings view, the settings toolbar shortcut and the read-only overview
// workspace. The backend part, ProxmoxPlugin, keeps the MCP endpoint, the consent gate and the engine, and answers
// this part's questions over the plugin's channel.
public sealed class ProxmoxUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new ProxmoxSettings(host.Storage);

        host.AddSettings(() => new ProxmoxSettingsControl(host, settings));
        host.AddToolbarAction(new ToolbarAction("Proxmox settings", MaterialIconKind.Server, () => host.ShowSettingsAsync()));

        // Read-only surface over the same gate/engine as the MCP tools — no second way to reach the API, just
        // reached over the channel instead of in-process now.
        host.AddWorkspaceType(new WorkspaceTypeRegistration("proxmox.overview", "Proxmox", context => new ProxmoxOverviewBody(context, host))
        {
            IconKind = MaterialIconKind.Server,
            Description = "Nodes, VMs, LXC containers and storage for a configured Proxmox target.",
        });

        // A settings save may have changed the target or its trusted certificate; tell the backend part to drop
        // its cached client so the next call rebuilds it. ponytail: OnSettingsSaved only offers a synchronous
        // callback, so it cannot await; blocking is safe today (the channel answers in-process with no thread hop).
        host.OnSettingsSaved(() => host.Channel.InvokeAsync(
            ProxmoxChannel.InvalidateTarget,
            JsonSerializer.SerializeToElement(new ProxmoxEmptyRequest(), ProxmoxChannel.Json)).GetAwaiter().GetResult());
    }
}
