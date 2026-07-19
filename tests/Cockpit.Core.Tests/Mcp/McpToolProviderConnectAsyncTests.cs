using System.Diagnostics;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpToolProvider.ConnectAsync"/> against real in-process MCP HTTP servers (#26): connecting
/// several enabled servers happens in parallel rather than one-by-one, and a server that cannot be reached
/// is skipped without stopping the others from coming through. Two separate tests, on purpose — a server
/// that fails to connect (below) can take its own, sometimes slow, time to give up, which would make a
/// single combined timing assertion flaky; the parallelism proof therefore only ever times reachable servers.
/// </summary>
public class McpToolProviderConnectAsyncTests
{
    // Each reachable server sleeps every request (initialize, tools/list, ...) by this much.
    private static readonly TimeSpan DelayPerServer = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task ConnectAsync_ConnectsEnabledServers_InParallel()
    {
        await using var serverA = await InProcessMcpHttpServer.StartAsync<McpTestToolA>(DelayPerServer);
        await using var serverB = await InProcessMcpHttpServer.StartAsync<McpTestToolB>(DelayPerServer);
        var soloProvider = _ProviderFor(_DisableBuiltIns().Append(
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Http, Url = serverA.Url }));
        var bothProvider = _ProviderFor(_DisableBuiltIns().Concat(
        [
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Http, Url = serverA.Url },
            new McpServerConfig { Name = "server-b", Transport = McpTransport.Http, Url = serverB.Url },
        ]));

        // Warm up JIT/connection-pool costs on an untimed connect first, so the two timed connects below
        // (one server vs. two) are comparable — a cold first HTTP call is not representative of the rest.
        await (await soloProvider.ConnectAsync()).DisposeAsync();

        var soloStopwatch = Stopwatch.StartNew();
        await (await soloProvider.ConnectAsync()).DisposeAsync();
        soloStopwatch.Stop();

        var bothStopwatch = Stopwatch.StartNew();
        await using var session = await bothProvider.ConnectAsync();
        bothStopwatch.Stop();

        // Both connected, in the same order the servers were listed (deterministic despite racing in parallel).
        session.ConnectedServerNames.Should().Equal("server-a", "server-b");
        session.Tools.Select(tool => tool.Name).Should().Contain(["tool_a", "tool_b"]);

        // A sequential connect of two servers would take roughly double a single server's connect time; well
        // under that (vs. the just-measured single-server baseline) proves the two connects overlapped rather
        // than running one after another. The 1.6x slack absorbs normal timing noise without hiding a real
        // regression to sequential (which would land close to 2x).
        bothStopwatch.Elapsed.Should().BeLessThan(soloStopwatch.Elapsed * 1.6);
    }

    [Fact]
    public async Task ConnectAsync_WithASessionSelection_ConnectsOnlyTheNamedServers()
    {
        await using var serverA = await InProcessMcpHttpServer.StartAsync<McpTestToolA>();
        await using var serverB = await InProcessMcpHttpServer.StartAsync<McpTestToolB>();
        var provider = _ProviderFor(_DisableBuiltIns().Concat(
        [
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Http, Url = serverA.Url },
            new McpServerConfig { Name = "server-b", Transport = McpTransport.Http, Url = serverB.Url },
        ]));

        // The per-session selection (#44) excludes server-b — on top of both being registry-enabled.
        await using var session = await provider.ConnectAsync(new HashSet<string> { "server-a" });

        session.ConnectedServerNames.Should().Equal("server-a");
        session.Tools.Should().ContainSingle().Which.Name.Should().Be("tool_a");
    }

    [Fact]
    public async Task ConnectAsync_SkipsAnUnreachableServer_WhileStillConnectingTheOthers()
    {
        await using var serverA = await InProcessMcpHttpServer.StartAsync<McpTestToolA>();
        var provider = _ProviderFor(_DisableBuiltIns().Concat(
        [
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Http, Url = serverA.Url },
            // Nothing listens on this loopback port — McpClient.CreateAsync fails to connect.
            new McpServerConfig { Name = "server-fail", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" },
        ]));

        await using var session = await provider.ConnectAsync();

        session.ConnectedServerNames.Should().Equal("server-a");
        session.Tools.Should().ContainSingle().Which.Name.Should().Be("tool_a");
    }

    /// <summary>Disables the built-in stdio presets (npx/uvx) — irrelevant here and not guaranteed available on a test machine.</summary>
    private static IReadOnlyList<McpServerConfig> _DisableBuiltIns() =>
        [.. McpServerPresets.LocalDefaults.Select(server => server with { Enabled = false })];

    private static McpToolProvider _ProviderFor(IEnumerable<McpServerConfig> registry)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(registry.ToList());
        return new McpToolProvider(catalog, Substitute.For<IMcpOAuthAuthorizer>(), new McpAuthKey(), new SessionMcpKeyring(), NullLogger<McpToolProvider>.Instance);
    }
}
