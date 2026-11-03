extern alias backend;

using System.Text.Json;
using NSubstitute;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-1400 (F2.12): the windows reach the access registries through the plugin's channel only, and an open_* tool
// with no UI part says nothing opened. F2.13/AC-1401 removed the IL scan for a UI type reaching ICockpitHost.Services:
// the physical split now makes that structurally impossible — see DiagramAssemblyLayoutTests.
[Collection("avalonia")]
public class DiagramChannelTests
{

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

    // AC-1891 (layer 2, plugin channel): the hub runs each call on its own pool thread, so two quick sends from a
    // window would race to the registry; the client queues them. The first one is held long enough to be overtaken.
    [Fact]
    public async Task TwoQuickTextUpdates_ReachTheRegistryInTheOrderTheWindowSentThem()
    {
        var registry = Substitute.For<IDiagramAccessRegistry>();
        var arrived = new List<string>();
        registry.When(r => r.UpdateText("d1", Arg.Any<string>())).Do(call =>
        {
            Thread.Sleep(call.ArgAt<string>(1) == "first" ? 500 : 0);
            lock (arrived)
            {
                arrived.Add(call.ArgAt<string>(1));
            }
        });
        var client = new DiagramChannelClient(TestChannel.For(diagrams: registry));

        client.UpdateText("d1", "first");
        client.UpdateText("d1", "second");
        await client.IsCoupledByAnotherAsync("pane-a", "d1");

        Assert.Equal(["first", "second"], arrived);
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
}
