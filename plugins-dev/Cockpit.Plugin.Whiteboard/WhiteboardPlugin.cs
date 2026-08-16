using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Whiteboard;

// Plugin-schil (AC-822): a workspace panel hosting AC-821's canvas, plus a toolbar action — same shape as
// AC-809's Diagram plugin, whose ALC measurement this reuses unchanged.
public sealed class WhiteboardPlugin : ICockpitPlugin
{
    private const string WorkspaceTypeId = "whiteboard.panel";

    public PluginMetadata Metadata { get; } = new(
        Id: "whiteboard",
        DisplayName: "Whiteboard",
        Author: "Cockpit",
        Description: "A freehand whiteboard panel: pencil strokes, shape templates and pasted screenshots.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddWorkspaceType(new WorkspaceTypeRegistration(WorkspaceTypeId, "Whiteboard", context => new WhiteboardWorkspaceBody(context, host))
        {
            IconKind = MaterialIconKind.Pencil,
            Description = "A freehand whiteboard: pencil, shape templates, pasted screenshots.",
        });

        host.AddToolbarAction(new ToolbarAction("Whiteboard", MaterialIconKind.Pencil,
            () => host.OpenWorkspaceAsync(WorkspaceTypeId)));
    }

    public void Dispose()
    {
    }
}
