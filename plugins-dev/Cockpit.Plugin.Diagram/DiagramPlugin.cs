using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Diagram;

// Plugin-schil proof (AC-809): a workspace panel plus a toolbar action, so both host surfaces are exercised.
public sealed class DiagramPlugin : ICockpitPlugin
{
    private const string WorkspaceTypeId = "diagram.panel";
    private const string ListWorkspaceTypeId = "diagram.list";

    public PluginMetadata Metadata { get; } = new(
        Id: "diagram",
        DisplayName: "Diagram Builder",
        Author: "Cockpit",
        Description: "Renders a Mermaid-syntax diagram in a workspace panel.");

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

        // AC-816: replaces the plain "Diagram Builder" open with a one-screen quick-start (name + optional session).
        host.AddToolbarAction(new ToolbarAction("Nieuw diagram", MaterialIconKind.Sitemap, () => _QuickStartAsync(host)));

        host.AddToolbarAction(new ToolbarAction("Diagrams", MaterialIconKind.FormatListBulleted,
            () => host.OpenWorkspaceAsync(ListWorkspaceTypeId)));
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
