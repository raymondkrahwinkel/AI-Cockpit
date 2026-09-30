extern alias backend;

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using NSubstitute;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Infrastructure.Diagrams;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using DiagramMcpTools = backend::Cockpit.Plugin.Diagram.DiagramMcpTools;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-1400 (F2.12): the windows reach the access registries through the plugin's channel only, and an open_* tool
// with no UI part says nothing opened. F2.13/AC-1401 removed the IL scan for a UI type reaching ICockpitHost.Services:
// the physical split now makes that structurally impossible — see DiagramAssemblyLayoutTests.
[Collection("avalonia")]
public class DiagramChannelTests
{
    private const string Flow = "flowchart LR\n  A-->B";

    [Fact]
    public void AnObjectPlacedOutOfOrder_IsStillDelivered_SinceSkippingItWouldLoseTheObject()
    {
        var (channel, deliver) = _HandDeliveredChannel();
        var client = new WhiteboardChannelClient(channel);
        var placed = new List<string>();
        client.ObjectPlaced += (_, objectId, _) => placed.Add(objectId);
        var placement = new WhiteboardPlacement("sticky", "Idee", 1, 2, 3, 4);

        deliver(new PluginChannelEvent("whiteboard.ObjectPlaced", 5, _Payload("b1", "o-5", placement)));
        deliver(new PluginChannelEvent("whiteboard.ObjectPlaced", 4, _Payload("b1", "o-4", placement)));

        Assert.Equal(["o-5", "o-4"], placed);
    }

    [Fact]
    public void CouplingASurfaceAnotherAgentHolds_IsRefusedInTheWindow_AndTheHolderKeepsIt()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("d1", "Flow", Flow);
        registry.Couple("pane-a", "d1");
        var client = new DiagramChannelClient(TestChannel.For(diagrams: registry));

        var refusal = Assert.Throws<InvalidOperationException>(() => client.Couple("pane-b", "d1"));

        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
        Assert.NotNull(registry.CouplingOf("pane-a", "d1"));
        Assert.Null(registry.CouplingOf("pane-b", "d1"));
    }

    [Fact]
    public async Task OpenDiagram_OnABackendWithNoUiPart_AnswersOkButNotOpened_WithoutAskingTheOperator()
    {
        var host = Substitute.For<ICockpitHost, IWindowProbe>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        var registry = new DiagramAccessRegistry();
        var settings = new backend::Cockpit.Plugin.Diagram.DiagramSettings(new FakePluginStorage());
        var tools = new DiagramMcpTools(host, registry, settings, TestChannel.Wire(host, diagrams: registry).Backend);

        var json = JsonNode.Parse(await tools.OpenDiagram("pane-a", "Onboarding flow", Flow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(true, json?["ok"]?.GetValue<bool>());
        Assert.Equal(false, json?["opened"]?.GetValue<bool>());
        await host.DidNotReceive().RequestConsentAsync(Arg.Any<ConsentRequest>());
        await host.DidNotReceive().ShowDialogAsync(Arg.Any<string>(), Arg.Any<Func<Control>>(), Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>());
    }

    private static JsonElement _Payload(params object[] args) => JsonSerializer.SerializeToElement(args, backend::Cockpit.Plugin.Diagram.DiagramChannelContract.Json);

    // A UI channel whose events the test hands in itself, seq and all — the order a racing publisher could produce.
    private static (IPluginUiChannel Channel, Action<PluginChannelEvent> Deliver) _HandDeliveredChannel()
    {
        var handlers = new Dictionary<string, Action<PluginChannelEvent>>();
        var channel = Substitute.For<IPluginUiChannel>();
        channel.Subscribe(Arg.Any<string>(), Arg.Any<Action<PluginChannelEvent>>()).Returns(call =>
        {
            handlers[call.ArgAt<string>(0)] = call.ArgAt<Action<PluginChannelEvent>>(1);
            return Substitute.For<IDisposable>();
        });
        return (channel, channelEvent => handlers[channelEvent.Name](channelEvent));
    }

    private sealed class _Sessions : ICockpitSessionObserver
    {
        private readonly List<OpenCockpitSession> _open = [];

        public string? ActiveSessionWorkingDirectory => null;

        public IReadOnlyList<OpenCockpitSession> OpenSessions => _open;

        public event EventHandler? ActiveSessionChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionOutputText>? OutputProduced
        {
            add { }
            remove { }
        }

        public event EventHandler<string>? SessionClosed;

        public void Open(string paneId, string name) => _open.Add(new OpenCockpitSession(paneId, name));

        public void Close(string paneId) => SessionClosed?.Invoke(this, paneId);
    }
}
