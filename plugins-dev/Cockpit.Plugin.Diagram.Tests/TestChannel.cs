extern alias backend;

using System.Text.Json;
using Avalonia.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;
using DiagramChannel = backend::Cockpit.Plugin.Diagram.DiagramChannel;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-1400: Diagram's channel as the host builds it — a real PluginChannelHub with DiagramChannel on its backend half
// — so a test hands a window the same UI half the plugin does, over whichever registries (fake or real) it needs.
// F2.13/AC-1401: DiagramChannel lives in the backend assembly, aliased "backend" in the csproj so an unqualified
// DiagramSettings (linked into both projects) is not ambiguous — see the csproj comment.
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
    // F2.13/AC-1401: SurfaceWindowOpener takes ICockpitUiHost, not the backend's ICockpitHost, so the UI host here
    // is its own substitute — its ShowDialogAsync forwards to `host`'s, the one these tests already assert against,
    // exactly as calling it on the pre-split combined host once did.
    public static DiagramChannel Desktop(
        ICockpitHost host,
        IDiagramAccessRegistry? diagrams = null,
        IWhiteboardAccessRegistry? whiteboards = null,
        IWireframeAccessRegistry? wireframes = null)
    {
        var (backend, ui) = Wire(host, diagrams, whiteboards, wireframes);
        var uiHost = Substitute.For<ICockpitUiHost>();
        uiHost.ShowDialogAsync(Arg.Any<string>(), Arg.Any<Func<Control>>(), Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>())
            .Returns(call => host.ShowDialogAsync(
                call.ArgAt<string>(0), call.ArgAt<Func<Control>>(1), call.ArgAt<string>(2), call.ArgAt<double>(3), call.ArgAt<double>(4)));
        _ = new SurfaceWindowOpener(uiHost, ui);
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
