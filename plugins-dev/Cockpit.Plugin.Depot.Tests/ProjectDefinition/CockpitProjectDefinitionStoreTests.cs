using Cockpit.Plugin.Depot.ProjectDefinition;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

public class CockpitProjectDefinitionStoreTests
{
    [Fact]
    public async Task ReadAsync_Success_CallsReadWithTheReservedPathAndParsesTheDefinition()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync("Depot: Synvolution", "read", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success(
                """{"path":".cockpit/project.json","content":"{\"schemaVersion\":1,\"name\":\"probe\"}","checksum":"abc123","size":30}""")));

        var result = await CockpitProjectDefinitionStore.ReadAsync(host, "Depot: Synvolution", "cockpit");

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal("probe", result.Definition!.Name);
        Assert.Equal("abc123", result.Checksum);
        await host.Received(1).CallMcpToolAsync(
            "Depot: Synvolution", "read",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => (string)args!["project"]! == "cockpit" && (string)args["path"]! == ".cockpit/project.json"),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadAsync_NotSignedIn_ReportsAuthorizationRequired()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.AuthorizationRequired));

        var result = await CockpitProjectDefinitionStore.ReadAsync(host, "Depot: Synvolution", "cockpit");

        Assert.Equal(PluginMcpToolCallOutcome.AuthorizationRequired, result.Outcome);
        Assert.Null(result.Definition);
    }

    [Fact]
    public async Task ReadAsync_ToolCallFails_ReportsFailedWithTheServersOwnMessage()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("not found")));

        var result = await CockpitProjectDefinitionStore.ReadAsync(host, "Depot: Synvolution", "cockpit");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal("not found", result.Error);
    }

    [Fact]
    public async Task ReadAsync_ContentIsNotValidDefinitionJson_ReportsFailedRatherThanThrowing()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":"x","content":"not json","checksum":"abc"}""")));

        var result = await CockpitProjectDefinitionStore.ReadAsync(host, "Depot: Synvolution", "cockpit");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ReadAsync_EnvelopeMissingChecksum_ReportsFailedRatherThanThrowing()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":"x","content":"{}"}""")));

        var result = await CockpitProjectDefinitionStore.ReadAsync(host, "Depot: Synvolution", "cockpit");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task WriteAsync_BaseChecksumProvided_IsPassedToTheWriteTool()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":".cockpit/project.json","checksum":"new123","bytesWritten":10}""")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Synvolution", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: "old123");

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal("new123", result.Checksum);
        await host.Received(1).CallMcpToolAsync(
            "Depot: Synvolution", "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => (string)args!["baseChecksum"]! == "old123"),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_NoBaseChecksum_OmitsItFromTheArguments()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":"x","checksum":"first","bytesWritten":1}""")));

        await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Synvolution", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        await host.Received(1).CallMcpToolAsync(
            "Depot: Synvolution", "write",
            Arg.Is<IReadOnlyDictionary<string, object?>?>(args => !args!.ContainsKey("baseChecksum")),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_ChecksumMismatch_ClassifiesAsChecksumConflict()
    {
        // The exact sentence Depot's own WriteFileCommandHandler sends back on a baseChecksum mismatch — measured
        // live against a real Depot server (AC-247), not guessed: write, read, write-with-correct-baseChecksum,
        // then a stale-baseChecksum write was rejected (content unchanged on re-read) and cross-checked against
        // Depot's own source (Depot.Application/Modules/Storage/Commands/WriteFile/WriteFileCommand.cs).
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed(
                "'.cockpit/project.json' changed since it was read; current checksum is abc999. Re-read and retry.")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Synvolution", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: "stale");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.ChecksumConflict, result.FailureKind);
    }

    [Fact]
    public async Task WriteAsync_RoleBelowEditor_ClassifiesAsPermissionDenied()
    {
        // Depot's own ProjectMemberAccessGuard phrasing for a role-too-low write (source:
        // Depot.Infrastructure/Access/ProjectMemberAccessGuard.cs) — this store's own callerRole pre-check (below)
        // never reaches Depot at all, so this is the case where Depot's own enforcement is the only line of defense
        // (e.g. a stale/unsupplied callerRole).
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("This action requires the Editor role on project 'cockpit'.")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Synvolution", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.PermissionDenied, result.FailureKind);
    }

    [Fact]
    public async Task WriteAsync_NotAProjectMember_ClassifiesAsPermissionDenied()
    {
        // Depot's non-member phrasing, same source as above.
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("You are not a member of project 'cockpit'.")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Synvolution", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.PermissionDenied, result.FailureKind);
    }

    [Fact]
    public async Task WriteAsync_UnrelatedFailure_ClassifiesAsUnclassified()
    {
        // A failure whose text matches neither known Depot shape must not be guessed into either bucket.
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("Could not connect to \"Depot: Synvolution\".")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Synvolution", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.Unclassified, result.FailureKind);
    }

    [Fact]
    public async Task WriteAsync_CallerRoleIsViewer_NeverCallsDepotAndNamesTheReason()
    {
        var host = Substitute.For<ICockpitHost>();

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Synvolution", "cockpit", new CockpitProjectDefinition { Name = "probe" },
            baseChecksum: "abc", callerRole: CockpitProjectRole.Viewer);

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal(CockpitProjectDefinitionWriteFailureKind.PermissionDenied, result.FailureKind);
        Assert.NotNull(result.Error);
        await host.DidNotReceive().CallMcpToolAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CockpitProjectRole.Editor)]
    [InlineData(CockpitProjectRole.Owner)]
    public async Task WriteAsync_CallerRoleCanWrite_ProceedsToCallDepot(CockpitProjectRole role)
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Success("""{"path":"x","checksum":"c1","bytesWritten":1}""")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Synvolution", "cockpit", new CockpitProjectDefinition { Name = "probe" },
            baseChecksum: "abc", callerRole: role);

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        await host.Received(1).CallMcpToolAsync(
            Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_NotSignedIn_ReportsAuthorizationRequired()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.AuthorizationRequired));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Synvolution", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: null);

        Assert.Equal(PluginMcpToolCallOutcome.AuthorizationRequired, result.Outcome);
    }
}
