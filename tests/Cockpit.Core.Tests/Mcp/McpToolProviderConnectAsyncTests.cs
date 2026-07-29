using System.Diagnostics;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions.Tty;
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
        Assert.Equal(new[] { "server-a", "server-b" }, session.ConnectedServerNames);
        var toolNames = session.Tools.Select(tool => tool.Name).ToList();
        Assert.Contains("tool_a", toolNames);
        Assert.Contains("tool_b", toolNames);

        // A sequential connect of two servers would take roughly double a single server's connect time; well
        // under that (vs. the just-measured single-server baseline) proves the two connects overlapped rather
        // than running one after another. The 1.6x slack absorbs normal timing noise without hiding a real
        // regression to sequential (which would land close to 2x).
        Assert.True(bothStopwatch.Elapsed < soloStopwatch.Elapsed * 1.6);
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

        Assert.Equal(new[] { "server-a" }, session.ConnectedServerNames);
        Assert.Equal("tool_a", Assert.Single(session.Tools).Name);
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

        Assert.Equal(new[] { "server-a" }, session.ConnectedServerNames);
        Assert.Equal("tool_a", Assert.Single(session.Tools).Name);
    }

    // AC-500 acceptance criterion 3: an OAuth server whose sign-in never happened must not disappear into the same
    // generic "could not connect" warning as any other unreachable server — it is a named outcome the session
    // exposes, so a caller can tell "no tools from this server" apart from "this server is waiting on a sign-in".
    [Fact]
    public async Task ConnectAsync_AnOAuthServerThatNeverSignedIn_IsReportedAsNeedingSignIn_NotJustUnreachable()
    {
        await using var serverA = await InProcessMcpHttpServer.StartAsync<McpTestToolA>();
        var provider = _ProviderFor(_DisableBuiltIns().Concat(
        [
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Http, Url = serverA.Url },
            // A plain (non-OAuth) unreachable server, alongside the OAuth one below — this is what proves the
            // guard actually checks Auth == OAuth rather than "any connect failure": without that check, this
            // server would land in ServersNeedingSignIn too.
            new McpServerConfig { Name = "server-fail", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" },
            // FakeMcpOAuthAuthorizer hands back options with a redirect nobody answers, so this fails at the
            // transport the same way a real "no stored token, nobody to ask" OAuth negotiation would.
            new McpServerConfig { Name = "server-oauth", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", Auth = McpServerAuth.OAuth },
        ]), oauthAuthorizer: new FakeMcpOAuthAuthorizer());

        await using var session = await provider.ConnectAsync();

        Assert.Equal(new[] { "server-a" }, session.ConnectedServerNames);
        Assert.Equal(new[] { "server-oauth" }, session.ServersNeedingSignIn);
    }

    /// <summary>Disables the built-in stdio presets (npx/uvx) — irrelevant here and not guaranteed available on a test machine.</summary>
    private static IReadOnlyList<McpServerConfig> _DisableBuiltIns() =>
        [.. McpServerPresets.LocalDefaults.Select(server => server with { Enabled = false })];

    // AC-218: the local-model tool loop is the third fan-out point, and the one whose other tests all pass
    // Arg.Any for the project — without this, reverting it to the unscoped catalog would leave the suite green.
    [Fact]
    public async Task ConnectAsync_ResolvesTheRegistryAsTheProjectSeesIt()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<McpServerConfig>());
        var provider = new McpToolProvider(
            catalog, Substitute.For<IMcpOAuthAuthorizer>(), new McpAuthKey(), new SessionMcpKeyring(), NullLogger<McpToolProvider>.Instance);

        await using var session = await provider.ConnectAsync(projectId: "project-1");

        await catalog.Received().GetServersForProjectAsync("project-1", Arg.Any<CancellationToken>());
    }

    // AC-143: McpToolProvider is the other remaining mint site the SessionMcpKeyring class doc used to flag as "not
    // yet covered". The mutation-style guard is the LivePaneCount assertion after DisposeAsync — it fails red if the
    // Revoke call is deleted from McpToolSession.DisposeAsync, unlike a bare "no exception" check.
    [Fact]
    public async Task ConnectAsync_WithAPaneId_MintsAKeyringTokenThatIsRevokedWhenTheSessionIsDisposed()
    {
        var keyring = new SessionMcpKeyring();
        var provider = _ProviderFor(_DisableBuiltIns(), keyring);

        var session = await provider.ConnectAsync(paneId: "local-model-pane-under-test");

        Assert.Equal(1, keyring.LivePaneCount);

        await session.DisposeAsync();

        Assert.Equal(0, keyring.LivePaneCount);
        Assert.Equal(0, keyring.LiveTokenCount);
    }

    // A connect with no pane id (no session to name) never touches the keyring — nothing was minted, so disposing
    // must not throw trying to revoke something that was never there.
    [Fact]
    public async Task ConnectAsync_WithoutAPaneId_NeverTouchesTheKeyring()
    {
        var keyring = new SessionMcpKeyring();
        var provider = _ProviderFor(_DisableBuiltIns(), keyring);

        var session = await provider.ConnectAsync();
        await session.DisposeAsync();

        Assert.Equal(0, keyring.LivePaneCount);
        Assert.Equal(0, keyring.LiveTokenCount);
    }

    // AC-143 full lifecycle (acceptance criterion 2): both remaining mint sites — the TTY route and this in-process
    // loop — share one keyring in a long-lived cockpit session; once every session they minted for has closed, the
    // keyring holds nothing at all, proven on the same ledger both routes actually write to rather than reasoned
    // about from each Revoke call in isolation.
    [Fact]
    public async Task ConnectAsync_AlongsideATtySession_BothRevokeAndLeaveTheSharedKeyringEmpty()
    {
        var keyring = new SessionMcpKeyring();
        var provider = _ProviderFor(_DisableBuiltIns(), keyring);
        var ptyHostFactory = Substitute.For<IPtyHostFactory>();
        ptyHostFactory
            .Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(Substitute.For<IConPtyProcess>());
        var ttyLauncher = new TtyLauncher(ptyHostFactory, new McpAuthKey(), keyring, NullLogger<TtyLauncher>.Instance);
        var ttyProvider = Substitute.For<ITtySessionProvider>();
        ttyProvider.ProviderId.Returns("test-provider");
        ttyProvider.BuildLaunch(Arg.Any<TtyLaunchContext>())
            .Returns(new TtyLaunchSpec("/usr/bin/cli", [], new Dictionary<string, string?>(), "/wd", []));

        var ttyProcess = ttyLauncher.Launch(ttyProvider, profile: null, options: new Dictionary<string, string>(), columns: 80, rows: 24, paneId: "tty-pane");
        var toolSession = await provider.ConnectAsync(paneId: "local-model-pane");

        Assert.Equal(2, keyring.LivePaneCount);

        ttyProcess.Dispose();
        await toolSession.DisposeAsync();

        Assert.Equal(0, keyring.LivePaneCount);
        Assert.Equal(0, keyring.LiveTokenCount);
    }

    private static McpToolProvider _ProviderFor(IEnumerable<McpServerConfig> registry, SessionMcpKeyring? keyring = null, IMcpOAuthAuthorizer? oauthAuthorizer = null)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(registry.ToList());
        return new McpToolProvider(catalog, oauthAuthorizer ?? Substitute.For<IMcpOAuthAuthorizer>(), new McpAuthKey(), keyring ?? new SessionMcpKeyring(), NullLogger<McpToolProvider>.Instance);
    }
}
