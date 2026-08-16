using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Diagram;

// Plugin-schil proof (AC-809): a workspace panel plus a toolbar action, so both host surfaces are exercised.
// AC-836 folded the whiteboard in here as a second surface — same shell, its own registry, capabilities, MCP
// server and consent text, so the agent never sees one document that changes shape.
public sealed class DiagramPlugin : ICockpitPlugin
{
    private const string WorkspaceTypeId = "diagram.panel";
    private const string ListWorkspaceTypeId = "diagram.list";
    private const string WhiteboardWorkspaceTypeId = "whiteboard.panel";

    // The id stays "diagram" so an existing install gets this as an update, not as a second plugin (AC-836).
    public PluginMetadata Metadata { get; } = new(
        Id: "diagram",
        DisplayName: "Diagram & Whiteboard",
        Author: "Cockpit",
        Description: "Renders a Mermaid-syntax diagram, and a freehand whiteboard, in a workspace panel.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        // Still a tab, still with no session on it (AC-834 took the unconditional EmbedSession out; whether the tab
        // survives at all is AC-850's call). AC-826's list hands its pick through DiagramOpenHandoff.
        host.AddWorkspaceType(new WorkspaceTypeRegistration(WorkspaceTypeId, "Diagram", context =>
        {
            var pending = DiagramOpenHandoff.Pending;
            DiagramOpenHandoff.Pending = null;
            var document = new DiagramDocument(
                context.WorkspaceId,
                pending?.Title ?? "Diagram",
                pending?.MermaidText ?? DiagramDocument.Sample);

            return new DiagramWorkspaceBody(host, document, sessionPaneId: null);
        })
        {
            IconKind = MaterialIconKind.Sitemap,
            Description = "A diagram rendered from Mermaid syntax.",
        });

        // AC-826: the project's diagrams, read via AC-812's file convention across AC-827's Memory rows.
        host.AddWorkspaceType(new WorkspaceTypeRegistration(ListWorkspaceTypeId, "Diagrams", context => new DiagramListWorkspaceBody(context, host))
        {
            IconKind = MaterialIconKind.FormatListBulleted,
            Description = "Every diagram saved in this project's memory.",
        });

        // AC-822's surface, unchanged by the merge (AC-836): its own workspace type, its own registry.
        host.AddWorkspaceType(new WorkspaceTypeRegistration(WhiteboardWorkspaceTypeId, "Whiteboard", context => new WhiteboardWorkspaceBody(host, context.WorkspaceId, sessionPaneId: null))
        {
            IconKind = MaterialIconKind.Pencil,
            Description = "A freehand whiteboard: pencil, shape templates, pasted screenshots.",
        });

        // AC-816: replaces the plain "Diagram Builder" open with a one-screen quick-start (name + optional session).
        host.AddToolbarAction(new ToolbarAction("Nieuw diagram", MaterialIconKind.Sitemap, () => _QuickStartAsync(host)));

        host.AddToolbarAction(new ToolbarAction("Diagrams", MaterialIconKind.FormatListBulleted,
            () => host.OpenWorkspaceAsync(ListWorkspaceTypeId)));

        // AC-842: a window beside the cockpit, not a tab — bound to whatever session is already active, no
        // separate session-picker step (the window model fixes the coupling at open time).
        host.AddToolbarAction(new ToolbarAction("Whiteboard", MaterialIconKind.Pencil,
            () => WhiteboardWindow.OpenAsync(host, host.Sessions.ActivePaneId)));
    }

    // AC-834: the quick-start's two answers — a name and a session that is already running — are exactly what a
    // diagram window needs, so it opens one instead of a tab. Which entry points exist at all is AC-850's.
    private async Task _QuickStartAsync(ICockpitHost host)
    {
        if (await DiagramQuickStartDialog.ShowAsync(host, "Nieuw diagram") is not { } quickStart)
        {
            return;
        }

        await DiagramWindow.OpenAsync(host, DiagramDocument.New(quickStart.Name), quickStart.SessionPaneId);
    }

    public void Dispose()
    {
    }
}
