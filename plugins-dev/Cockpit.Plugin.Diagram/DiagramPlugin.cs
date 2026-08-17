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

        // AC-873: the wireframe surface, same window-not-tab shape as the other two.
        host.AddToolbarAction(new ToolbarAction("Nieuw wireframe", MaterialIconKind.ViewGridOutline, () => _WireframeQuickStartAsync(host)));
        host.AddToolbarAction(new ToolbarAction("Wireframes", MaterialIconKind.FormatListBulleted,
            () => host.ShowDialogAsync("Wireframes", () => new WireframeListDialogBody(), WireframeListDialogKey, width: 520, height: 600)));

        _ListenForAgentOpenRequests(host);
    }

    // AC-835: the MCP tools live in core and cannot open a plugin window, so the access registry — already the only
    // seam between the two — carries the request over. The operator has approved it by the time it arrives here.
    private void _ListenForAgentOpenRequests(ICockpitHost host)
    {
        if (host.Services.GetService(typeof(IDiagramAccessRegistry)) is IDiagramAccessRegistry diagrams)
        {
            _diagrams = diagrams;
            _onDiagramOpen = request => Dispatcher.UIThread.Post(() =>
                _ = DiagramWindow.OpenAsync(host, new DiagramDocument(request.SurfaceId, request.Name, request.Text), request.SessionId));
            diagrams.OpenRequested += _onDiagramOpen;
        }

        if (host.Services.GetService(typeof(IWhiteboardAccessRegistry)) is IWhiteboardAccessRegistry whiteboards)
        {
            _whiteboards = whiteboards;
            _onWhiteboardOpen = request => Dispatcher.UIThread.Post(() =>
                _ = WhiteboardWindow.OpenAsync(host, new WhiteboardDocument(request.SurfaceId, request.Name), request.SessionId));
            whiteboards.OpenRequested += _onWhiteboardOpen;
        }

        if (host.Services.GetService(typeof(IWireframeAccessRegistry)) is IWireframeAccessRegistry wireframes)
        {
            _wireframes = wireframes;
            _onWireframeOpen = request => Dispatcher.UIThread.Post(() =>
                _ = WireframeWindow.OpenAsync(host, new WireframeDocument(request.SurfaceId, request.Name, request.Text), request.SessionId));
            wireframes.OpenRequested += _onWireframeOpen;
        }
    }

    private IDiagramAccessRegistry? _diagrams;
    private IWhiteboardAccessRegistry? _whiteboards;
    private IWireframeAccessRegistry? _wireframes;
    private Action<DiagramOpenRequest>? _onDiagramOpen;
    private Action<WhiteboardOpenRequest>? _onWhiteboardOpen;
    private Action<WireframeOpenRequest>? _onWireframeOpen;

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

    // AC-873: DiagramPlugin._QuickStartAsync's counterpart — a quick-started wireframe opens with the single
    // childless "screen" line (WireframeDocument.Empty), never a bare document with nothing to render.
    private async Task _WireframeQuickStartAsync(ICockpitHost host)
    {
        if (await WireframeQuickStartDialog.ShowAsync(host, "Nieuw wireframe") is not { } quickStart)
        {
            return;
        }

        await WireframeWindow.OpenAsync(host, WireframeDocument.New(quickStart.Name), quickStart.SessionPaneId);
    }

    public void Dispose()
    {
        if (_onDiagramOpen is not null && _diagrams is not null)
        {
            _diagrams.OpenRequested -= _onDiagramOpen;
        }

        if (_onWhiteboardOpen is not null && _whiteboards is not null)
        {
            _whiteboards.OpenRequested -= _onWhiteboardOpen;
        }

        if (_onWireframeOpen is not null && _wireframes is not null)
        {
            _wireframes.OpenRequested -= _onWireframeOpen;
        }
    }
}
