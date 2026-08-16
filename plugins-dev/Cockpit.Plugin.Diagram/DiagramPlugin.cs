using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Diagram;

// Plugin-schil proof (AC-809): a toolbar action plus a window, so both host surfaces are exercised.
// AC-836 folded the whiteboard in here as a second surface — same shell, its own registry, capabilities, MCP
// server and consent text, so the agent never sees one document that changes shape.
public sealed class DiagramPlugin : ICockpitPlugin
{
    // Reused as the diagrams dialog's ShowDialogAsync singleInstanceKey — one list at a time (AC-850).
    private const string ListDialogKey = "diagram.list";

    // The id stays "diagram" so an existing install gets this as an update, not as a second plugin (AC-836).
    public PluginMetadata Metadata { get; } = new(
        Id: "diagram",
        DisplayName: "Diagram & Whiteboard",
        Author: "Cockpit",
        Description: "Opens a Mermaid diagram, or a freehand whiteboard, in its own window beside the cockpit.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        // AC-850: the Diagram/Whiteboard tabs and the diagrams-list tab are gone — every ⋯ item now opens a
        // window or a dialog instead of a workspace.
        host.AddToolbarAction(new ToolbarAction("Nieuw diagram", MaterialIconKind.Sitemap, () => _QuickStartAsync(host)));

        // AC-826's list, now a dialog rather than a workspace.
        host.AddToolbarAction(new ToolbarAction("Diagrams", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Diagrams", () => new DiagramListDialogBody(host), ListDialogKey, width: 520, height: 600)));

        // AC-842: a window beside the cockpit, not a tab — bound to whatever session is already active, no
        // separate session-picker step (the window model fixes the coupling at open time).
        host.AddToolbarAction(new ToolbarAction("Whiteboard", MaterialIconKind.Pencil, () => _OpenWhiteboardAsync(host)));
    }

    // AC-834: the quick-start's two answers — a name and a session that is already running — are exactly what a
    // diagram window needs, so it opens one instead of a tab.
    private async Task _QuickStartAsync(ICockpitHost host)
    {
        if (await DiagramQuickStartDialog.ShowAsync(host, "Nieuw diagram") is not { } quickStart)
        {
            return;
        }

        await DiagramWindow.OpenAsync(host, DiagramDocument.New(quickStart.Name), quickStart.SessionPaneId);
    }

    // AC-850: nothing here starts a session — no active one means a toast, not a window bound to nothing.
    private static Task _OpenWhiteboardAsync(ICockpitHost host)
    {
        if (host.Sessions.ActivePaneId is not { } paneId)
        {
            host.ShowToast("Geen actieve sessie om het whiteboard aan te koppelen.", PluginToastSeverity.Information);
            return Task.CompletedTask;
        }

        return WhiteboardWindow.OpenAsync(host, paneId);
    }

    public void Dispose()
    {
    }
}
