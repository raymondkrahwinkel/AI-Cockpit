using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Diagram;

// The plugin-schil for the diagram builder (AC-809, sub of AC-525): a workspace-type panel plus a toolbar
// quick action to open it, proving a store plugin can reach both host surfaces from one zip — not just
// whichever one happens to be tested first. What the panel renders is a fixed sample; what belongs in it is
// [e], not this ticket.
public sealed class DiagramPlugin : ICockpitPlugin
{
    private const string WorkspaceTypeId = "diagram.panel";

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
        host.AddWorkspaceType(new WorkspaceTypeRegistration(WorkspaceTypeId, "Diagram", context => new DiagramWorkspaceBody(context))
        {
            IconKind = MaterialIconKind.Sitemap,
            Description = "A diagram rendered from Mermaid syntax.",
        });

        host.AddToolbarAction(new ToolbarAction("Diagram Builder", MaterialIconKind.Sitemap,
            () => host.OpenWorkspaceAsync(WorkspaceTypeId)));
    }

    public void Dispose()
    {
    }
}
