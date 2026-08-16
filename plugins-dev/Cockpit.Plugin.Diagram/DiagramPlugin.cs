using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram;

// Plugin-schil proof (AC-809): a toolbar action plus a window, so both host surfaces are exercised.
// AC-836 folded the whiteboard in here as a second surface — same shell, its own registry, capabilities, MCP
// server and consent text, so the agent never sees one document that changes shape.
public sealed class DiagramPlugin : ICockpitPlugin
{
    // Reused as the diagrams dialog's ShowDialogAsync singleInstanceKey — one list at a time (AC-850).
    private const string ListDialogKey = "diagram.list";

    // W-2/AC-843: the whiteboards dialog's own singleInstanceKey, same precedent, one folder over.
    private const string WhiteboardListDialogKey = "whiteboard.list";

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
        // separate session-picker step (the window model fixes the coupling at open time). W-2/AC-843: named at
        // the door, same snelstart shape as the diagram's.
        host.AddToolbarAction(new ToolbarAction("Nieuw whiteboard", MaterialIconKind.Pencil, () => _WhiteboardQuickStartAsync(host)));

        // W-2/AC-843's list, DiagramListDialogBody's counterpart — how a saved board is reopened.
        host.AddToolbarAction(new ToolbarAction("Whiteboards", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Whiteboards", () => new WhiteboardListDialogBody(host), WhiteboardListDialogKey, width: 520, height: 600)));
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

    // W-2/AC-843: DiagramPlugin._QuickStartAsync's counterpart — an unsaved board starts empty, named for what
    // the operator asked for, and only ever gets a file once it is first saved (AC-812's rule, one folder over).
    private async Task _WhiteboardQuickStartAsync(ICockpitHost host)
    {
        if (await WhiteboardQuickStartDialog.ShowAsync(host, "Nieuw whiteboard") is not { } quickStart)
        {
            return;
        }

        await WhiteboardWindow.OpenAsync(host, new WhiteboardDocument(title: quickStart.Name), quickStart.SessionPaneId);
    }

    public void Dispose()
    {
    }
}
