using System.Text;
using System.Text.Json;
using Cockpit.App.Files;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.UI;
using NSubstitute;
using Cockpit.Infrastructure.BackendApi;
using System.Net;

namespace Cockpit.App.ViewTests;

// Layer 2, contract: the host client sees the same JSON boundary over a local channel or a server relay.
public class FileSourceChannelClientTests
{
    [Fact]
    public async Task FileChannel_RoundtripsSnapshots_AndKeepsBoundaryFailuresTypedAndSafe()
    {
        var channel = Substitute.For<IPluginUiChannel>();
        var client = new FileSourceChannelClient(channel);
        var file = new FileRef("c1", "opaque", "note.md");
        channel.InvokeAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(JsonSerializer.SerializeToElement(new FileSourceAnswer<FileSourceReadAnswer>(
                new(Convert.ToBase64String(Encoding.UTF8.GetBytes("# read")), "v1", 6, null, "text/markdown", CanWrite: true), null), FileSourceChannel.Json)));
        var read = await client.ReadAsync(file, default);
        using var reader = new StreamReader(read.Content);
        Assert.Equal("# read", await reader.ReadToEndAsync());
        Assert.Equal("v1", read.Version);
        Assert.True(read.CanWrite);
        channel.InvokeAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(JsonSerializer.SerializeToElement(new { data = new { contentBase64 = "", version = "old" } }, FileSourceChannel.Json)));
        var oldSnapshot = await client.ReadAsync(file, default);
        using var oldContent = oldSnapshot.Content;
        Assert.False(oldSnapshot.CanWrite);
        channel.InvokeAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(JsonSerializer.SerializeToElement(new FileSourceAnswer<FileWriteResult>(new(FileWriteStatus.Written, "v2"), null), FileSourceChannel.Json)));
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("# saved"));
        Assert.Equal(new FileWriteResult(FileWriteStatus.Written, "v2"), await client.WriteAsync(file, content, "v1", false, default));
        channel.InvokeAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<JsonElement>(new IOException("private-server-path")));
        var transport = await Assert.ThrowsAsync<FileSourceException>(() => client.ReadAsync(file, default));
        Assert.Equal(FileSourceErrorKind.Unavailable, transport.Kind);
        Assert.DoesNotContain("private-server-path", transport.Message, StringComparison.Ordinal);
        channel.InvokeAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<JsonElement>(new BackendApiException(HttpStatusCode.Unauthorized, "expired", "private-backend-description")));
        var backend = await Assert.ThrowsAsync<FileSourceException>(() => client.ReadAsync(file, default));
        Assert.Equal(FileSourceErrorKind.Unavailable, backend.Kind);
        Assert.DoesNotContain("private-backend-description", backend.Message, StringComparison.Ordinal);
        channel.InvokeAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(JsonSerializer.SerializeToElement("unexpected reply")));
        var malformed = await Assert.ThrowsAsync<FileSourceException>(() => client.ReadAsync(file, default));
        Assert.Equal(FileSourceErrorKind.Unavailable, malformed.Kind);
        channel.InvokeAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(JsonSerializer.SerializeToElement(new FileSourceAnswer<FileSourceReadAnswer>(
                new("!", "v1", null, null, null), null), FileSourceChannel.Json)));
        var base64 = await Assert.ThrowsAsync<FileSourceException>(() => client.ReadAsync(file, default));
        Assert.Equal(FileSourceErrorKind.Unavailable, base64.Kind);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        channel.InvokeAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromCanceled<JsonElement>(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadAsync(file, cancelled.Token));
    }
}
