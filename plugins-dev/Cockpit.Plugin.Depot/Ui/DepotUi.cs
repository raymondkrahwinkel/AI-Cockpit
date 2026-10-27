using System.Diagnostics;
using Avalonia.Controls;
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
    // Read by Cockpit's `--screenshot --scene plugin:<name>` on a dev checkout only; not a plugin contract.
    public static IReadOnlyDictionary<string, Func<Control>> Scenes => DepotScenes.All;

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
        host.Channel.Subscribe(DepotChannel.OpenSettings, evt => Dispatcher.UIThread.Post(() =>
        {
            // Channel handlers are synchronous, so the wrapper observes and logs the detached settings task.
            _ = _ShowSettingsAsync(host);
        }));
        // AC-784: same global-toolbar route the Kubernetes plugin uses (AC-91) — the project editor's own
        // "Servers…" button above still opens settings the same way it always has.
        host.AddToolbarAction(new ToolbarAction("Depot settings", MaterialIconKind.Database, () => host.ShowSettingsAsync()));
        // AC-1586: one inbox model for the plugin's lifetime, so the side-menu badge sums every connection's unread.
        var inbox = new DepotInboxModel(host);
        var badge = host.AddSideMenuButtonWithBadge("Depot inbox", () =>
        {
            // Side-menu callbacks are synchronous, so the wrapper observes and logs the detached dialog task.
            _ = _ShowInboxAsync(host, settings, inbox);
        }, MaterialIconKind.InboxOutline);
        inbox.Changed += _ => badge.Primary = inbox.Total;
        // Startup continues while counts load; LoadCountsAsync logs failures and a later push can refresh the badge.
        _ = inbox.LoadCountsAsync();

        // AC-1568: the file browser, as a dock panel and from the toolbar.
        host.AddDockPanel(new DockPanelRegistration("depot-browser", "Depot files", MaterialIconKind.FolderNetworkOutline, () => new DepotBrowserPanel(host, settings.Connections, inbox)));
        host.AddLinkHandler(new LinkHandlerRegistration(
            (string link, out int start, out int length) => DepotTranscriptLinks.TryMatch(link, settings.Connections.Select(connection => connection.Url).ToList(), out start, out length),
            (_, _) =>
            {
                _ = host.OpenDockPanelAsync("depot-browser");
                return true;
            }));
        host.AddToolbarAction(new ToolbarAction("Depot files", MaterialIconKind.FolderNetworkOutline,
            () => host.ShowDialogAsync("Depot files", () => new DepotBrowserPanel(host, settings.Connections, inbox), "depot-browser", 1000, 680)));
    }

    private static async Task _ShowSettingsAsync(ICockpitUiHost host)
    {
        try
        {
            await host.ShowSettingsAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Opening Depot settings failed: {exception}");
        }
    }

    private static async Task _ShowInboxAsync(ICockpitUiHost host, DepotSettings settings, DepotInboxModel inbox)
    {
        try
        {
            await host.ShowDialogAsync(
                "Depot", () => new DepotBrowserPanel(host, settings.Connections, inbox, openInbox: true), "depot-browser", 1000, 680);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Opening the Depot inbox failed: {exception}");
        }
    }
}
