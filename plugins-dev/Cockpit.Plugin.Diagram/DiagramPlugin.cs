using Avalonia.Threading;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Wireframe;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Diagram;

// Plugin-schil proof (AC-809); AC-836/AC-864 folded whiteboard and wireframe in as further surfaces, same shell each.
// AC-1400: one assembly with both entry points until F2.13 — Initialize owns the registries, their channel and the
// MCP tools; InitializeUi hands the windows that channel, their only way in.
public sealed class DiagramPlugin : ICockpitPlugin, ICockpitPluginUi
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

    private ICockpitHost? _host;
    private DiagramChannel? _channel;
    private IPluginUiChannel? _uiChannel;
    private IDisposable? _openSubscription;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        _host = host;
        // AC-850: the Diagram/Whiteboard tabs and the diagrams-list tab are gone — every ⋯ item now opens a
        // window or a dialog instead of a workspace. AC-896: the "New ..." actions moved into their panel's
        // own header, next to Refresh — only the panel openers stay here.
        host.AddToolbarAction(new ToolbarAction("Diagrams", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Diagrams", () => new DiagramListDialogBody(host, _uiChannel), ListDialogKey, width: 520, height: 600)));

        host.AddToolbarAction(new ToolbarAction("Whiteboards", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Whiteboards", () => new WhiteboardListDialogBody(host, _uiChannel), WhiteboardListDialogKey, width: 520, height: 600)));

        host.AddToolbarAction(new ToolbarAction("Wireframes", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Wireframes", () => new WireframeListDialogBody(host, _uiChannel), WireframeListDialogKey, width: 520, height: 600)));

        var settings = new DiagramSettings(host.Storage);
        host.AddSettings(() => new DiagramSettingsControl(host, settings));

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

    public void InitializeUi(ICockpitUiHost host)
    {
        _uiChannel = host.Channel;
        _openSubscription = host.Channel.Subscribe(DiagramChannel.OpenSurface, _OnOpenSurface);
        host.Channel.InvokeAsync(DiagramChannel.AttachUi, default).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _openSubscription?.Dispose();
        _channel?.Dispose();
    }

    // An agent's open_* tool asked for a window (after the operator's consent, in the backend part). Posted, since
    // the event arrives on the tool's thread; the window couples to the calling session the way it always did.
    private void _OnOpenSurface(PluginChannelEvent channelEvent)
    {
        if (_host is not { } host)
        {
            return;
        }

        var payload = channelEvent.Payload;
        var surfaceId = DiagramChannel.ReadString(payload, 0);
        var kind = DiagramChannel.ReadString(payload, 1);
        var title = DiagramChannel.ReadString(payload, 2);
        var source = payload[3].GetString() ?? "";
        var caller = DiagramChannel.ReadString(payload, 4);
        var channel = _uiChannel;
        Dispatcher.UIThread.Post(() => _ = kind switch
        {
            DiagramChannel.WhiteboardPrefix => WhiteboardWindow.OpenAsync(host, channel, new WhiteboardDocument(surfaceId, title), caller),
            DiagramChannel.WireframePrefix => WireframeWindow.OpenAsync(host, channel, new WireframeDocument(surfaceId, title, source), caller),
            _ => DiagramWindow.OpenAsync(host, channel, new DiagramDocument(surfaceId, title, source), caller),
        });
    }
}
