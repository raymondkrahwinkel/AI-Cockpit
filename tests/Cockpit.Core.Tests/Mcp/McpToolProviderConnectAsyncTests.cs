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
    // Each reachable server sleeps every request (initialize, tools/list, ...) by this much — long enough that the
    // recorded request windows below (see InProcessMcpHttpServer.RequestWindows) have a comfortable margin to prove
    // overlap even under scheduling jitter.
    private static readonly TimeSpan DelayPerServer = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task ConnectAsync_ConnectsEnabledServers_InParallel()
    {
        await using var serverA = await InProcessMcpHttpServer.StartAsync<McpTestToolA>(DelayPerServer);
        await using var serverB = await InProcessMcpHttpServer.StartAsync<McpTestToolB>(DelayPerServer);
        var bothProvider = _ProviderFor(_DisableBuiltIns().Concat(
        [
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Http, Url = serverA.Url },
            new McpServerConfig { Name = "server-b", Transport = McpTransport.Http, Url = serverB.Url },
        ]));

        await using var session = await bothProvider.ConnectAsync();

        // Both connected, in the same order the servers were listed (deterministic despite racing in parallel).
        Assert.Equal(new[] { "server-a", "server-b" }, session.ConnectedServerNames);
        var toolNames = session.Tools.Select(tool => tool.Function.Name).ToList();
        Assert.Contains("tool_a", toolNames);
        Assert.Contains("tool_b", toolNames);

        // Direct proof of overlap rather than an elapsed-time ratio against a separately measured baseline: each
        // server's own request-window recordings show exactly when its requests were in flight. A genuine regression
        // to sequential connects (server B's handshake starting only once server A's has fully finished) cannot
        // produce overlapping windows no matter how a busy runner stretches both measurements — a wall-clock ratio
        // can be fooled by exactly that kind of asymmetric noise, which is what made this test flake on CI.
        Assert.True(_RequestsOverlapped(serverA.RequestWindows, serverB.RequestWindows));
    }

    /// <summary>Whether any recorded request window from one server overlaps any from the other in wall-clock time.</summary>
    private static bool _RequestsOverlapped(
        IReadOnlyCollection<(DateTimeOffset Start, DateTimeOffset End)> a,
        IReadOnlyCollection<(DateTimeOffset Start, DateTimeOffset End)> b) =>
        a.Any(windowA => b.Any(windowB => windowA.Start < windowB.End && windowB.Start < windowA.End));

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
        Assert.Equal("tool_a", Assert.Single(session.Tools).Function.Name);
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
        Assert.Equal("tool_a", Assert.Single(session.Tools).Function.Name);
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

    // AC-997: a server that fails to connect for any other reason (unreachable, or a stdio server that starts and
    // then exits) is a named outcome too — a caller reporting the mount upstream needs the name and a short reason
    // rather than nothing at all, which is what let the operator's own selection get silently rewritten around it.
    [Fact]
    public async Task ConnectAsync_AnUnreachableServer_IsReportedAsAConnectionIssue_WithAShortOneLineReason()
    {
        var provider = _ProviderFor(_DisableBuiltIns().Concat(
        [
            // Nothing listens on this loopback port — McpClient.CreateAsync fails to connect.
            new McpServerConfig { Name = "server-fail", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" },
        ]));

        await using var session = await provider.ConnectAsync();

        var issue = Assert.Single(session.ConnectionIssues);
        Assert.Equal("server-fail", issue.Name);
        Assert.False(string.IsNullOrWhiteSpace(issue.Reason));
        Assert.DoesNotContain('\n', issue.Reason);
        Assert.Empty(session.ServersNeedingSignIn);
    }

    // Same acceptance criterion, other half: the existing AC-500 outcome must reach this new list too, so a
    // caller reporting connection issues upstream does not have to also read ServersNeedingSignIn separately.
    [Fact]
    public async Task ConnectAsync_AnOAuthServerThatNeverSignedIn_AlsoAppearsInConnectionIssues()
    {
        var provider = _ProviderFor(
            _DisableBuiltIns().Append(new McpServerConfig { Name = "server-oauth", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", Auth = McpServerAuth.OAuth }),
            oauthAuthorizer: new FakeMcpOAuthAuthorizer());

        await using var session = await provider.ConnectAsync();

        var issue = Assert.Single(session.ConnectionIssues);
        Assert.Equal("server-oauth", issue.Name);
        Assert.False(string.IsNullOrWhiteSpace(issue.Reason));
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
            catalog, Substitute.For<IMcpOAuthAuthorizer>(), Substitute.For<IMcpOAuthCoordinator>(), new McpAuthKey(), new SessionMcpKeyring(), NullLogger<McpToolProvider>.Instance);

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
        var ttyLauncher = new TtyLauncher(ptyHostFactory, Substitute.For<ISessionMemoryLimiter>(), new McpAuthKey(), keyring, NullLogger<TtyLauncher>.Instance);
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

    // AC-505 follow-up: the widened DiscoverProbeTimeout/InitializationTimeout pairing (McpInteractiveOAuthClientOptions)
    // is only worth paying when a sign-in might actually run — a server whose GetStateAsync already reports a usable
    // token connects on the SDK's fast default instead, so a merely slow (not down) OAuth server cannot stall the
    // whole session-connect Task.WhenAll for the widened window on every session start. Verified by checking the
    // coordinator is actually consulted before the connect, which is what decides that fork.
    [Fact]
    public async Task ConnectAsync_ForAnOAuthServer_AsksTheCoordinatorWhetherASignInIsNeeded_BeforeConnecting()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);
        var provider = _ProviderFor(
            _DisableBuiltIns().Append(new McpServerConfig { Name = "server-oauth", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", Auth = McpServerAuth.OAuth }),
            oauthAuthorizer: new FakeMcpOAuthAuthorizer(),
            oauthCoordinator: coordinator);

        await using var session = await provider.ConnectAsync();

        await coordinator.Received(1).GetStateAsync(Arg.Is<McpServerConfig>(server => server.Name == "server-oauth"), Arg.Any<CancellationToken>());
    }

    private static McpToolProvider _ProviderFor(IEnumerable<McpServerConfig> registry, SessionMcpKeyring? keyring = null, IMcpOAuthAuthorizer? oauthAuthorizer = null, IMcpOAuthCoordinator? oauthCoordinator = null)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(registry.ToList());
        return new McpToolProvider(
            catalog,
            oauthAuthorizer ?? Substitute.For<IMcpOAuthAuthorizer>(),
            oauthCoordinator ?? Substitute.For<IMcpOAuthCoordinator>(),
            new McpAuthKey(),
            keyring ?? new SessionMcpKeyring(),
            NullLogger<McpToolProvider>.Instance);
    }
}
