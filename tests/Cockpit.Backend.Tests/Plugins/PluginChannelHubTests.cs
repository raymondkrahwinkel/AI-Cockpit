using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Files;
using System.Text;
using NSubstitute;

namespace Cockpit.Backend.Tests.Plugins;

// AC-1389 (F2.1): the channel between a plugin's backend and UI part — per plugin, JSON only, events numbered
// on the backend's one counter.
public class PluginChannelHubTests
{
    // Also the handle: after a reload the plugin removes its handler and registers the action again, which throws
    // while the first registration is still in place.
    [Fact]
    public async Task InvokeAsync_ReachesTheSamePluginsHandler_AndReturnsItsAnswer_UntilItIsRemovedAndRegisteredAgain()
    {
        var hub = new PluginChannelHub(NullLogger<PluginChannelHub>.Instance);
        var registration = hub.For("diagram").Handle("list", (payload, _) =>
            Task.FromResult(JsonSerializer.SerializeToElement($"listed {payload.GetString()}")));

        var answer = await hub.InvokeAsync("diagram", "list", JsonSerializer.SerializeToElement("boards"), CancellationToken.None);
        registration.Dispose();
        using var reloaded = hub.For("diagram").Handle("list", (_, _) => Task.FromResult(JsonSerializer.SerializeToElement("reloaded")));
        var afterReload = await hub.InvokeAsync("diagram", "list", JsonSerializer.SerializeToElement("boards"), CancellationToken.None);

        Assert.Equal(("listed boards", "reloaded"), (answer.GetString(), afterReload.GetString()));
        await _FileSourceRoundtripAsync(hub);
    }

    private static async Task _FileSourceRoundtripAsync(PluginChannelHub hub)
    {
        var source = Substitute.For<IFileSource>();
        source.Capabilities.Returns(new FileSourceCapabilities(true, FileSourceChannel.MaxContentBytes));
        source.ReadAsync(Arg.Any<FileRef>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromResult(new FileReadResult(new MemoryStream(Encoding.UTF8.GetBytes("# hello")), "v1")));
        source.WriteAsync(Arg.Any<FileRef>(), Arg.Any<Stream>(), "v1", false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new FileWriteResult(FileWriteStatus.Conflict, "v2")));
        var provider = Substitute.For<IFileSourceProvider>();
        provider.Connections.Returns([new FileConnection("c1", "Fake", "memory", false)]);
        provider.Open("c1").Returns(source);
        using var files = Cockpit.Infrastructure.Files.FileSourceChannel.Register(hub.For("files"), provider);
        var file = new FileRef("c1", "opaque", "note.md");
        var read = await hub.InvokeAsync("files", FileSourceChannel.Read,
            JsonSerializer.SerializeToElement(new FileSourceReadRequest(file), FileSourceChannel.Json), default);
        var content = read.Deserialize<FileSourceAnswer<FileSourceReadAnswer>>(FileSourceChannel.Json)!;
        Assert.Equal("# hello", Encoding.UTF8.GetString(Convert.FromBase64String(content.Data!.ContentBase64)));
        Assert.Equal("v1", content.Data.Version);
        var stale = await hub.InvokeAsync("files", FileSourceChannel.Write,
            JsonSerializer.SerializeToElement(new FileSourceWriteRequest(file, "", "v1", false), FileSourceChannel.Json), default);
        Assert.Equal(FileWriteStatus.Conflict, stale.Deserialize<FileSourceAnswer<FileWriteResult>>(FileSourceChannel.Json)!.Data!.Status);
        var invalid = await hub.InvokeAsync("files", FileSourceChannel.Write,
            JsonSerializer.SerializeToElement(new FileSourceWriteRequest(file, "", null, false), FileSourceChannel.Json), default);
        Assert.NotNull(invalid.Deserialize<FileSourceAnswer<FileWriteResult>>(FileSourceChannel.Json)!.Error);
        provider.Connections.Returns([new FileConnection("c1", "Fake", "memory", true)]);
        var forbidden = await hub.InvokeAsync("files", FileSourceChannel.Write,
            JsonSerializer.SerializeToElement(new FileSourceWriteRequest(file, "", "v1", false), FileSourceChannel.Json), default);
        Assert.Equal(FileSourceErrorKind.Forbidden, forbidden.Deserialize<FileSourceAnswer<FileWriteResult>>(FileSourceChannel.Json)!.Error!.Kind);
        await source.Received(1).WriteAsync(Arg.Any<FileRef>(), Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_OnAnActionOnlyAnotherPluginHandles_IsAnUnknownAction_AndNeverRunsTheirHandler()
    {
        var hub = new PluginChannelHub(NullLogger<PluginChannelHub>.Instance);
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

    // A subscriber that throws, subscribed first, costs the one after it nothing and never reaches the publisher.
    [Fact]
    public void Publish_ReachesThatPluginsSubscribers_WithRisingSeq_AndNoOtherPlugins()
    {
        var hub = new PluginChannelHub(NullLogger<PluginChannelHub>.Instance);
        var received = new List<PluginChannelEvent>();
        var receivedByOther = new List<PluginChannelEvent>();
        using var throwing = hub.Subscribe("diagram", "changed", _ => throw new InvalidOperationException("broken subscriber"));
        using var subscription = hub.Subscribe("diagram", "changed", received.Add);
        using var otherSubscription = hub.Subscribe("youtrack", "changed", receivedByOther.Add);

        hub.For("diagram").Publish("changed", JsonSerializer.SerializeToElement(1));
        hub.For("diagram").Publish("changed", JsonSerializer.SerializeToElement(2));

        Assert.Equal([1, 2], received.Select(channelEvent => channelEvent.Payload.GetInt32()));
        Assert.True(received[1].Seq > received[0].Seq, $"seq {received[1].Seq} after {received[0].Seq}");
        Assert.Empty(receivedByOther);
    }
}
