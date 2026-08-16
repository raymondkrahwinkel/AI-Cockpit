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
        host.AddWorkspaceType(new WorkspaceTypeRegistration(WorkspaceTypeId, "Diagram", context => new DiagramWorkspaceBody(context, host))
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

        host.AddToolbarAction(new ToolbarAction("Diagram Builder", MaterialIconKind.Sitemap,
            () => host.OpenWorkspaceAsync(WorkspaceTypeId)));

        host.AddToolbarAction(new ToolbarAction("Diagrams", MaterialIconKind.FormatListBulleted,
            () => host.OpenWorkspaceAsync(ListWorkspaceTypeId)));
    }

    public void Dispose()
    {
    }
}
