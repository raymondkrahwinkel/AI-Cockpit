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

    /// <summary>Disables the built-in stdio presets (npx/uvx) — irrelevant here and not guaranteed available on a test machine.</summary>
    private static IReadOnlyList<McpServerConfig> _DisableBuiltIns() =>
        [.. McpServerPresets.LocalDefaults.Select(server => server with { Enabled = false })];

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
