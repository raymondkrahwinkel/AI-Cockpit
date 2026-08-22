using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Wireframe;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram;

// Plugin-schil proof (AC-809): a toolbar action plus a window, so both host surfaces are exercised.
// AC-836/AC-864 folded whiteboard and wireframe in as a second and third surface, same shell each.
public sealed class DiagramPlugin : ICockpitPlugin
{
    // Reused as the diagrams dialog's ShowDialogAsync singleInstanceKey — one list at a time (AC-850).
    private const string ListDialogKey = "diagram.list";

    // W-2/AC-843: the whiteboards dialog's own singleInstanceKey, same precedent, one folder over.
    private const string WhiteboardListDialogKey = "whiteboard.list";

    // AC-873: the wireframes dialog's own singleInstanceKey, same precedent, another folder over.
    private const string WireframeListDialogKey = "wireframe.list";

    // The id stays "diagram" so an existing install gets this as an update, not as a second plugin (AC-836/AC-864).
    public PluginMetadata Metadata { get; } = new(
        Id: "diagram",
        DisplayName: "Diagram, Whiteboard & Wireframe",
        Author: "Cockpit",
        Description: "Opens a Mermaid diagram, a freehand whiteboard, or a wireframe sketch, in its own window beside the cockpit.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        // AC-850: the Diagram/Whiteboard tabs and the diagrams-list tab are gone — every ⋯ item now opens a
        // window or a dialog instead of a workspace. AC-896: the "New ..." actions moved into their panel's
        // own header, next to Refresh — only the panel openers stay here.
        host.AddToolbarAction(new ToolbarAction("Diagrams", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Diagrams", () => new DiagramListDialogBody(host), ListDialogKey, width: 520, height: 600)));

        host.AddToolbarAction(new ToolbarAction("Whiteboards", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Whiteboards", () => new WhiteboardListDialogBody(host), WhiteboardListDialogKey, width: 520, height: 600)));

        host.AddToolbarAction(new ToolbarAction("Wireframes", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Wireframes", () => new WireframeListDialogBody(host), WireframeListDialogKey, width: 520, height: 600)));

        var settings = new DiagramSettings(host.Storage);
        host.AddSettings(() => new DiagramSettingsControl(host, settings));

        // AC-889/AC-890: mounted here rather than the host, so an install without this plugin does not offer
        // cockpit-diagram/-whiteboard/-wireframe at all. No isEnabled (AC-830 dropped the master switch) and no
        // isInternal — these are tickable servers for the operator, unlike Autopilot's own endpoints.
        if (host.Services.GetService(typeof(IDiagramAccessRegistry)) is IDiagramAccessRegistry diagrams)
        {
            _ = host.AddMcpEndpoint("cockpit-diagram", new DiagramMcpTools(host, diagrams, settings));
        }

        if (host.Services.GetService(typeof(IWhiteboardAccessRegistry)) is IWhiteboardAccessRegistry whiteboards)
        {
            _ = host.AddMcpEndpoint("cockpit-whiteboard", new WhiteboardMcpTools(host, whiteboards, settings));
        }

        if (host.Services.GetService(typeof(IWireframeAccessRegistry)) is IWireframeAccessRegistry wireframes)
        {
            _ = host.AddMcpEndpoint("cockpit-wireframe", new WireframeMcpTools(host, wireframes, settings));
        }
    }

    public void Dispose()
    {
    }
}
