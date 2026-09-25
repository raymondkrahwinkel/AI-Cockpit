using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram;

// Plugin-schil proof (AC-809); AC-836/AC-864 folded whiteboard and wireframe in as further surfaces, same shell each.
// F2.13 (AC-1401): the backend half — the registries, their channel and the MCP tools. DiagramUi (UI/DiagramUi.cs,
// its own assembly) owns the windows and everything else that needs Avalonia; AC-1400 grew the channel between them.
public sealed class DiagramPlugin : ICockpitPlugin
{
    // The id stays "diagram" so an existing install gets this as an update, not as a second plugin (AC-836/AC-864).
    public PluginMetadata Metadata { get; } = new(
        Id: "diagram",
        DisplayName: "Diagram, Whiteboard & Wireframe",
        Author: "Cockpit",
        Description: "Opens a Mermaid diagram, a freehand whiteboard, or a wireframe sketch, in its own window beside the cockpit.");

    private DiagramChannel? _channel;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new DiagramSettings(host.Storage);

        var diagrams = host.Services.GetService(typeof(IDiagramAccessRegistry)) as IDiagramAccessRegistry;
        var whiteboards = host.Services.GetService(typeof(IWhiteboardAccessRegistry)) as IWhiteboardAccessRegistry;
        var wireframes = host.Services.GetService(typeof(IWireframeAccessRegistry)) as IWireframeAccessRegistry;
        var channel = new DiagramChannel(host, diagrams, whiteboards, wireframes);
        _channel = channel;

        // AC-889/AC-890: mounted here rather than the host, so an install without this plugin does not offer
        // cockpit-diagram/-whiteboard/-wireframe at all. No isEnabled (AC-830 dropped the master switch) and no
        // isInternal — these are tickable servers for the operator, unlike Autopilot's own endpoints.
        if (diagrams is not null)
        {
            _ = host.AddMcpEndpoint("cockpit-diagram", new DiagramMcpTools(host, diagrams, settings, channel));
        }

        if (whiteboards is not null)
        {
            _ = host.AddMcpEndpoint("cockpit-whiteboard", new WhiteboardMcpTools(host, whiteboards, settings, channel));
        }

        if (wireframes is not null)
        {
            _ = host.AddMcpEndpoint("cockpit-wireframe", new WireframeMcpTools(host, wireframes, settings, channel));
        }
    }

    public void Dispose()
    {
        _channel?.Dispose();
    }
}
