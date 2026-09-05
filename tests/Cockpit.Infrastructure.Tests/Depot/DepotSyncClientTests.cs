using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Depot;
using Cockpit.Infrastructure.Depot;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Depot;

/// <summary>
/// One counter-example per AC-280 acceptance criterion: paginating the full listing (1), splitting missing from
/// unreadable on a batch read (2), distinguishing written/conflict/invalid per file on a batch write (3), a
/// failed round never registering as a silent success (4), and move/delete surfacing a checksum conflict (5).
/// </summary>
public sealed class DepotSyncClientTests
{
    private const string ServerName = "Depot: Work";
    private const string Project = "acme";

    [Fact]
    public async Task ListAllAsync_FollowsEveryPageAndReturnsEachEntrysFields()
    {
        var invoker = Substitute.For<IMcpToolInvoker>();
        invoker.InvokeAsync(ServerName, "list", Arg.Is<IReadOnlyDictionary<string, object?>>(a => a["after"] == null), null, null, Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.Success(
                """{"files":[{"path":"a.md","size":10,"updatedAt":"2026-09-01T00:00:00Z","checksum":"sha-a"}],"nextCursor":"a.md"}"""));
        invoker.InvokeAsync(ServerName, "list", Arg.Is<IReadOnlyDictionary<string, object?>>(a => (string?)a["after"] == "a.md"), null, null, Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.Success(
                """{"files":[{"path":"b.md","size":20,"updatedAt":"2026-09-02T00:00:00Z","checksum":"sha-b"}],"nextCursor":null}"""));
        var client = new DepotSyncClient(invoker, TimeSpan.Zero);

        var result = await client.ListAllAsync(ServerName, Project);

        Assert.Equal(DepotListOutcome.Success, result.Outcome);
        Assert.Equal(
            [new DepotFileEntry("a.md", 10, DateTimeOffset.Parse("2026-09-01T00:00:00Z"), "sha-a"),
             new DepotFileEntry("b.md", 20, DateTimeOffset.Parse("2026-09-02T00:00:00Z"), "sha-b")],
            result.Files);
    }

    [Fact]
    public async Task ReadManyAsync_SplitsMissingFromUnreadable()
    {
        var invoker = Substitute.For<IMcpToolInvoker>();
        invoker.InvokeAsync(ServerName, "read_many", Arg.Any<IReadOnlyDictionary<string, object?>>(), null, null, Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.Success(
                """{"files":[{"path":"found.md","content":"hi","checksum":"sha-found"}],"missing":["gone.md"]}"""));
        var client = new DepotSyncClient(invoker, TimeSpan.Zero);

        var result = await client.ReadManyAsync(ServerName, Project, ["found.md", "gone.md"]);

        Assert.Equal(DepotReadManyOutcome.Success, result.Outcome);
        Assert.Equal([new DepotReadFile("found.md", "hi", "sha-found")], result.Files);
        Assert.Equal(["gone.md"], result.Missing);
        Assert.Empty(result.Unreadable);
    }

    [Fact]
    public async Task WriteManyAsync_DistinguishesWrittenConflictAndInvalidPerFile()
    {
        var invoker = Substitute.For<IMcpToolInvoker>();
        invoker.InvokeAsync(ServerName, "write_many", Arg.Any<IReadOnlyDictionary<string, object?>>(), null, null, Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.Success(
                """{"results":[{"path":"ok.md","status":"written","checksum":"sha-ok"},{"path":"stale.md","status":"conflict","message":"changed"},{"path":"bad.md","status":"invalid","message":"not markdown"}]}"""));
        var client = new DepotSyncClient(invoker, TimeSpan.Zero);
        var entries = new List<DepotWriteEntry>
        {
            new("ok.md", "content", "sha-old"),
            new("stale.md", "content", "sha-old"),
            new("bad.md", "content", null),
        };

        var result = await client.WriteManyAsync(ServerName, Project, entries);

        Assert.Equal(DepotWriteStatus.Written, result.Results.Single(r => r.Path == "ok.md").Status);
        Assert.Equal(DepotWriteStatus.Conflict, result.Results.Single(r => r.Path == "stale.md").Status);
        Assert.Equal(DepotWriteStatus.Invalid, result.Results.Single(r => r.Path == "bad.md").Status);
    }

    [Fact]
    public async Task WriteManyAsync_ARoundThatFailsOutright_NeverRegistersItsFilesAsWritten()
    {
        var invoker = Substitute.For<IMcpToolInvoker>();
        invoker.InvokeAsync(ServerName, "write_many", Arg.Any<IReadOnlyDictionary<string, object?>>(), null, null, Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.Failed("Depot is unreachable."));
        var client = new DepotSyncClient(invoker, TimeSpan.Zero);
        var entries = new List<DepotWriteEntry> { new("a.md", "content", null), new("b.md", "content", null) };

        var result = await client.WriteManyAsync(ServerName, Project, entries);

        Assert.All(result.Results, entry => Assert.Equal(DepotWriteStatus.Failed, entry.Status));
        Assert.Equal(["a.md", "b.md"], result.Results.Select(r => r.Path));
    }

    [Fact]
    public async Task MoveAndDeleteAsync_ReportAChecksumMismatchAsAConflict_UsingTheBaseChecksumTheyWereGiven()
    {
        var invoker = Substitute.For<IMcpToolInvoker>();
        invoker.InvokeAsync(ServerName, "move", Arg.Is<IReadOnlyDictionary<string, object?>>(a => (string?)a["baseChecksum"] == "sha-old"), null, null, Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.Failed("'a.md' changed since it was read; current checksum is sha-new. Re-read and retry."));
        invoker.InvokeAsync(ServerName, "delete", Arg.Is<IReadOnlyDictionary<string, object?>>(a => (string?)a["baseChecksum"] == "sha-old"), null, null, Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.Failed("'b.md' changed since it was read; current checksum is sha-new. Re-read and retry."));
        var client = new DepotSyncClient(invoker, TimeSpan.Zero);

        var moveResult = await client.MoveAsync(ServerName, Project, "a.md", "a2.md", "sha-old");
        var deleteResult = await client.DeleteAsync(ServerName, Project, "b.md", "sha-old");

        Assert.Equal(DepotMutationOutcome.Conflict, moveResult.Outcome);
        Assert.Equal(DepotMutationOutcome.Conflict, deleteResult.Outcome);
    }
}
