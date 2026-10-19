using Avalonia.Threading;
using Material.Icons;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Onboarding;

namespace Cockpit.Plugin.Depot.UI;

// The UI part of Depot (AC-1394): the settings view and the global-toolbar shortcut to it. The backend part,
// DepotPlugin, keeps the project-memory-source/shared-project-source/MCP-provider registrations and syncs them
// whenever this part reports a connection-list save over the plugin's channel.
public sealed class DepotUi : ICockpitPluginUi, IFirstStartPluginUi
{
    // AC-1509: route C, there from the very first start; the wizard runs before InitializeUi.
    public IReadOnlyList<FirstStartRoute> CreateFirstStartRoutes(IFirstStartHost host) =>
    [
        new(
            "depot.sign-in",
            "Sign in to a Depot",
            "Restore your latest backup, or set up from your profile and your organisation.",
            () => new DepotFirstStartView(host))
        {
            IconKind = MaterialIconKind.Cloud,
        },
    ];

    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new DepotSettings(host.Storage);
        host.AddSettings(() => new DepotSettingsControl(host, settings));
        // AC-1511: Admin → Plugins → Depot → Settings on a server, whose backend part this channel reaches.
        host.AddServerSettings(channel => new DepotServerSettingsControl(channel));
        host.Channel.Subscribe(DepotChannel.OpenSettings, evt => Dispatcher.UIThread.Post(() => _ = host.ShowSettingsAsync()));
        // AC-784: same global-toolbar route the Kubernetes plugin uses (AC-91) — the project editor's own
        // "Servers…" button above still opens settings the same way it always has.
        host.AddToolbarAction(new ToolbarAction("Depot settings", MaterialIconKind.Database, () => host.ShowSettingsAsync()));
        // AC-1568: the file browser, as a dock panel and from the toolbar.
        host.AddDockPanel(new DockPanelRegistration("depot-browser", "Depot files", MaterialIconKind.FolderNetworkOutline, () => new DepotBrowserPanel(host, settings.Connections)));
        host.AddToolbarAction(new ToolbarAction("Depot files", MaterialIconKind.FolderNetworkOutline,
            () => host.ShowDialogAsync("Depot files", () => new DepotBrowserPanel(host, settings.Connections), "depot-browser", 1000, 680)));
    }
}
