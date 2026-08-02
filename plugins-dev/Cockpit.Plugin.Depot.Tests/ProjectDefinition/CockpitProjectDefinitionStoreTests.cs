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
    public async Task WriteAsync_ChecksumMismatch_ReportsFailedWithTheServersOwnMessage()
    {
        // A baseChecksum conflict comes back through ICockpitHost.CallMcpToolAsync as an ordinary Failed outcome
        // (measured live against Depot, AC-244) — this store adds no separate conflict signal of its own.
        var host = Substitute.For<ICockpitHost>();
        host.CallMcpToolAsync(Arg.Any<string>(), "write", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PluginMcpToolCallResult.Failed("checksum mismatch")));

        var result = await CockpitProjectDefinitionStore.WriteAsync(
            host, "Depot: Synvolution", "cockpit", new CockpitProjectDefinition { Name = "probe" }, baseChecksum: "stale");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal("checksum mismatch", result.Error);
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
