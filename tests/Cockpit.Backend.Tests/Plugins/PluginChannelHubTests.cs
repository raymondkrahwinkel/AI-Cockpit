using System.Diagnostics;
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
    // while the first registration is still in place. Another plugin asking for the same action is refused and never
    // runs this plugin's handler.
    [Fact]
    public async Task InvokeAsync_ReachesOnlyThatPluginsHandler_AndReturnsItsAnswer_UntilItIsRemovedAndRegisteredAgain()
    {
        var hub = new PluginChannelHub(NullLogger<PluginChannelHub>.Instance);
        var calls = 0;
        var registration = hub.For("diagram").Handle("list", (payload, _) =>
        {
            calls++;
            return Task.FromResult(JsonSerializer.SerializeToElement($"listed {payload.GetString()}"));
        });

        var refused = await Assert.ThrowsAsync<PluginChannelUnknownActionException>(
            () => hub.InvokeAsync("youtrack", "list", default, CancellationToken.None));
        var answer = await hub.InvokeAsync("diagram", "list", JsonSerializer.SerializeToElement("boards"), CancellationToken.None);
        Assert.Equal(("youtrack", "list", 1), (refused.PluginId, refused.Action, calls));
        registration.Dispose();
        using var reloaded = hub.For("diagram").Handle("list", (_, _) => Task.FromResult(JsonSerializer.SerializeToElement("reloaded")));
        var afterReload = await hub.InvokeAsync("diagram", "list", JsonSerializer.SerializeToElement("boards"), CancellationToken.None);

        Assert.Equal(("listed boards", "reloaded"), (answer.GetString(), afterReload.GetString()));
        await _FileSourceRoundtripAsync(hub);
    }

    private static async Task _FileSourceRoundtripAsync(PluginChannelHub hub)
    {
        Assert.NotNull(typeof(FileReadResult).GetConstructor([typeof(Stream), typeof(string), typeof(long?), typeof(DateTimeOffset?), typeof(string)]));
        Assert.NotNull(typeof(FileSourceReadAnswer).GetConstructor([typeof(string), typeof(string), typeof(long?), typeof(DateTimeOffset?), typeof(string)]));
        var source = Substitute.For<IFileSource>();
        source.Capabilities.Returns(new FileSourceCapabilities(true, FileSourceChannel.MaxContentBytes));
        source.ReadAsync(Arg.Any<FileRef>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromResult(new FileReadResult(new MemoryStream(Encoding.UTF8.GetBytes("# hello")), "v1") { CanWrite = true }));
        source.WriteAsync(Arg.Any<FileRef>(), Arg.Any<Stream>(), "v1", false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new FileWriteResult(FileWriteStatus.Conflict, "v2")));
        var provider = Substitute.For<IFileSourceProvider>();
        provider.Connections.Returns([new FileConnection("c1", "Fake", "memory", false)]);
        provider.Open("c1").Returns(source);
        using var files = Cockpit.Infrastructure.Files.FileSourceChannel.Register(hub.For("files"), provider);
        var file = new FileRef("c1", "opaque", "note.md");
        var read = await hub.InvokeAsync("files", FileSourceChannel.Read,
            JsonSerializer.SerializeToElement(new FileSourceReadRequest(file), FileSourceChannel.Json), default);
        var content = read.Deserialize<FileSourceAnswer<FileSourceReadAnswer>>(FileSourceChannel.Json)
            ?? throw new InvalidOperationException("The read answer was missing.");
        Assert.NotNull(content.Data);
        Assert.Equal("# hello", Encoding.UTF8.GetString(Convert.FromBase64String(content.Data.ContentBase64)));
        Assert.Equal("v1", content.Data.Version);
        Assert.True(content.Data.CanWrite);
        var stale = await hub.InvokeAsync("files", FileSourceChannel.Write,
            JsonSerializer.SerializeToElement(new FileSourceWriteRequest(file, "", "v1", false), FileSourceChannel.Json), default);
        var staleResult = stale.Deserialize<FileSourceAnswer<FileWriteResult>>(FileSourceChannel.Json)?.Data
            ?? throw new InvalidOperationException("The write result was missing.");
        Assert.Equal(FileWriteStatus.Conflict, staleResult.Status);
        var invalid = await hub.InvokeAsync("files", FileSourceChannel.Write,
            JsonSerializer.SerializeToElement(new FileSourceWriteRequest(file, "", null, false), FileSourceChannel.Json), default);
        Assert.NotNull(invalid.Deserialize<FileSourceAnswer<FileWriteResult>>(FileSourceChannel.Json)?.Error);
        var malformed = await hub.InvokeAsync("files", FileSourceChannel.Write,
            JsonSerializer.SerializeToElement(new FileSourceWriteRequest(file, "!", "v1", false), FileSourceChannel.Json), default);
        Assert.Equal(FileSourceErrorKind.Unsupported, (malformed.Deserialize<FileSourceAnswer<FileWriteResult>>(FileSourceChannel.Json)?.Error
            ?? throw new InvalidOperationException("The malformed request error was missing.")).Kind);
        var oversized = await hub.InvokeAsync("files", FileSourceChannel.Write,
            JsonSerializer.SerializeToElement(new FileSourceWriteRequest(file,
                Convert.ToBase64String(new byte[FileSourceChannel.MaxContentBytes + 1]), "v1", false), FileSourceChannel.Json), default);
        Assert.Equal(FileSourceErrorKind.TooLarge, (oversized.Deserialize<FileSourceAnswer<FileWriteResult>>(FileSourceChannel.Json)?.Error
            ?? throw new InvalidOperationException("The oversized request error was missing.")).Kind);
        source.ReadAsync(Arg.Any<FileRef>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(
            new FileReadResult(new MemoryStream(new byte[FileSourceChannel.MaxContentBytes + 1]), "large")));
        var tooLargeRead = await hub.InvokeAsync("files", FileSourceChannel.Read,
            JsonSerializer.SerializeToElement(new FileSourceReadRequest(file), FileSourceChannel.Json), default);
        Assert.Equal(FileSourceErrorKind.TooLarge, (tooLargeRead.Deserialize<FileSourceAnswer<FileSourceReadAnswer>>(FileSourceChannel.Json)?.Error
            ?? throw new InvalidOperationException("The oversized read error was missing.")).Kind);
        provider.Connections.Returns([new FileConnection("c1", "Fake", "memory", true)]);
        var forbidden = await hub.InvokeAsync("files", FileSourceChannel.Write,
            JsonSerializer.SerializeToElement(new FileSourceWriteRequest(file, "", "v1", false), FileSourceChannel.Json), default);
        Assert.Equal(FileSourceErrorKind.Forbidden, (forbidden.Deserialize<FileSourceAnswer<FileWriteResult>>(FileSourceChannel.Json)?.Error
            ?? throw new InvalidOperationException("The permission error was missing.")).Kind);
        source.ReadAsync(Arg.Any<FileRef>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(
            new FileReadResult(new MemoryStream(), "v1") { CanWrite = true }));
        var readonlySnapshot = await hub.InvokeAsync("files", FileSourceChannel.Read,
            JsonSerializer.SerializeToElement(new FileSourceReadRequest(file), FileSourceChannel.Json), default);
        Assert.False((readonlySnapshot.Deserialize<FileSourceAnswer<FileSourceReadAnswer>>(FileSourceChannel.Json)?.Data
            ?? throw new InvalidOperationException("The readonly snapshot was missing.")).CanWrite);
        provider.Connections.Returns([new FileConnection("c1", "Fake", "memory", false)]);
        source.Capabilities.Returns(new FileSourceCapabilities(false, FileSourceChannel.MaxContentBytes));
        var incapableSnapshot = await hub.InvokeAsync("files", FileSourceChannel.Read,
            JsonSerializer.SerializeToElement(new FileSourceReadRequest(file), FileSourceChannel.Json), default);
        Assert.False((incapableSnapshot.Deserialize<FileSourceAnswer<FileSourceReadAnswer>>(FileSourceChannel.Json)?.Data
            ?? throw new InvalidOperationException("The source capability snapshot was missing.")).CanWrite);
        var readonlyConnections = await hub.InvokeAsync("files", FileSourceChannel.Connections,
            JsonSerializer.SerializeToElement(new { }), default);
        Assert.True(Assert.Single(readonlyConnections.Deserialize<FileSourceAnswer<FileConnection[]>>(FileSourceChannel.Json)?.Data
            ?? throw new InvalidOperationException("The connection list was missing.")).ReadOnly);
        await source.Received(1).WriteAsync(Arg.Any<FileRef>(), Arg.Any<Stream>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        var changes = new List<PluginChannelEvent>();
        using var subscription = hub.Subscribe("files", FileSourceChannel.Changed, changes.Add);
        provider.ConnectionsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);
        Assert.Single(changes);
        files.Dispose();
        provider.ConnectionsChanged += Raise.Event<EventHandler>(provider, EventArgs.Empty);
        Assert.Single(changes);
        await Assert.ThrowsAsync<PluginChannelUnknownActionException>(() => hub.InvokeAsync("files", FileSourceChannel.Read, default, default));
        using var reloaded = Cockpit.Infrastructure.Files.FileSourceChannel.Register(hub.For("files"), provider);
        await Assert.ThrowsAsync<PluginChannelUnknownActionException>(() => hub.InvokeAsync("other-plugin", FileSourceChannel.Read, default, default));
    }

    // AC-1891 (layer 2, plugin channel): a handler that blocks before its first await, as a backend's synchronous
    // I/O does, must not hold its caller — a UI control's call returns at once. Inline, it would return after 2 s.
    [Fact]
    public async Task InvokeAsync_NeverRunsTheHandlerOnTheCallersThread_SoABlockingHandlerDoesNotHoldTheCaller()
    {
        var hub = new PluginChannelHub(NullLogger<PluginChannelHub>.Instance);
        using var release = new ManualResetEventSlim();
        using var registration = hub.For("git").Handle("status", (payload, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(2));
            return Task.FromResult(payload);
        });

        var clock = Stopwatch.StartNew();
        var call = hub.InvokeAsync("git", "status", JsonSerializer.SerializeToElement("clean"), CancellationToken.None);
        var heldFor = clock.Elapsed;
        release.Set();

        Assert.Equal("clean", (await call).GetString());
        Assert.True(heldFor < TimeSpan.FromSeconds(1), $"the caller was held for {heldFor}");
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
