using System.Text.Json;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Backend.Tests.Plugins;

// AC-1389 (F2.1): the channel between a plugin's backend and UI part — per plugin, JSON only, events numbered
// on the backend's one counter.
public class PluginChannelHubTests
{
    [Fact]
    public async Task InvokeAsync_ReachesTheSamePluginsHandler_AndReturnsItsAnswer()
    {
        var hub = new PluginChannelHub();
        hub.For("diagram").Handle("list", (payload, _) =>
            Task.FromResult(JsonSerializer.SerializeToElement($"listed {payload.GetString()}")));

        var answer = await hub.InvokeAsync("diagram", "list", JsonSerializer.SerializeToElement("boards"), CancellationToken.None);

        Assert.Equal("listed boards", answer.GetString());
    }

    [Fact]
    public async Task InvokeAsync_OnAnActionOnlyAnotherPluginHandles_IsAnUnknownAction_AndNeverRunsTheirHandler()
    {
        var hub = new PluginChannelHub();
        var othersHandlerRan = false;
        hub.For("diagram").Handle("list", (payload, _) =>
        {
            othersHandlerRan = true;
            return Task.FromResult(payload);
        });

        var refused = await Assert.ThrowsAsync<PluginChannelUnknownActionException>(
            () => hub.InvokeAsync("youtrack", "list", default, CancellationToken.None));

        Assert.Equal(("youtrack", "list"), (refused.PluginId, refused.Action));
        Assert.False(othersHandlerRan);
    }

    [Fact]
    public void Publish_ReachesThatPluginsSubscribers_WithRisingSeq_AndNoOtherPlugins()
    {
        var hub = new PluginChannelHub();
        var received = new List<PluginChannelEvent>();
        var receivedByOther = new List<PluginChannelEvent>();
        using var subscription = hub.Subscribe("diagram", "changed", received.Add);
        using var otherSubscription = hub.Subscribe("youtrack", "changed", receivedByOther.Add);

        hub.For("diagram").Publish("changed", JsonSerializer.SerializeToElement(1));
        hub.For("diagram").Publish("changed", JsonSerializer.SerializeToElement(2));

        Assert.Equal([1, 2], received.Select(channelEvent => channelEvent.Payload.GetInt32()));
        Assert.True(received[1].Seq > received[0].Seq, $"seq {received[1].Seq} after {received[0].Seq}");
        Assert.Empty(receivedByOther);
    }
}
