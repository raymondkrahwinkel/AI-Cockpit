using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-1400: Diagram's channel as the host builds it — a real PluginChannelHub with DiagramChannel on its backend half
// — so a test hands a window the same UI half the plugin does, over whichever registries (fake or real) it needs.
internal static class TestChannel
{
    private const string PluginId = "diagram";

    public static IPluginUiChannel For(
        IDiagramAccessRegistry? diagrams = null,
        IWhiteboardAccessRegistry? whiteboards = null,
        IWireframeAccessRegistry? wireframes = null) =>
        Wire(Substitute.For<ICockpitHost>(), diagrams, whiteboards, wireframes).Ui;

    public static DiagramChannelClient Diagram(IDiagramAccessRegistry registry) => new(For(diagrams: registry));

    public static WhiteboardChannelClient Whiteboard(IWhiteboardAccessRegistry registry) => new(For(whiteboards: registry));

    // The desktop as it runs: the backend half, and a UI part that opens the window an open_* tool asks for.
    public static DiagramChannel Desktop(
        ICockpitHost host,
        IDiagramAccessRegistry? diagrams = null,
        IWhiteboardAccessRegistry? whiteboards = null,
        IWireframeAccessRegistry? wireframes = null)
    {
        var (backend, ui) = Wire(host, diagrams, whiteboards, wireframes);
        _ = new SurfaceWindowOpener(host, ui);
        return backend;
    }

    // Makes `host` (a substitute) carry the backend half, as CockpitHost.Channel does for the real plugin.
    public static (DiagramChannel Backend, IPluginUiChannel Ui) Wire(
        ICockpitHost host,
        IDiagramAccessRegistry? diagrams = null,
        IWhiteboardAccessRegistry? whiteboards = null,
        IWireframeAccessRegistry? wireframes = null)
    {
        var hub = new PluginChannelHub(NullLogger<PluginChannelHub>.Instance);
        host.Channel.Returns(hub.For(PluginId));
        return (new DiagramChannel(host, diagrams, whiteboards, wireframes), new UiChannel(hub));
    }

    private sealed class UiChannel(PluginChannelHub hub) : IPluginUiChannel
    {
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            hub.InvokeAsync(PluginId, action, payload, cancellationToken);

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => hub.Subscribe(PluginId, name, handler);
    }
}
