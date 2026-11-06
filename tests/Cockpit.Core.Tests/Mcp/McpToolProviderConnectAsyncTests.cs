using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions.Tty;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
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
    /// <summary>Disables the built-in stdio presets (npx/uvx) — irrelevant here and not guaranteed available on a test machine.</summary>
    private static IReadOnlyList<McpServerConfig> _DisableBuiltIns() =>
        [.. McpServerPresets.LocalDefaults.Select(server => server with { Enabled = false })];

    // AC-143 full lifecycle (acceptance criterion 2): both remaining mint sites — the TTY route and this in-process
    // loop — share one keyring in a long-lived cockpit session; once every session they minted for has closed, the
    // keyring holds nothing at all, proven on the same ledger both routes actually write to rather than reasoned
    // about from each Revoke call in isolation.
    [Fact]
    public async Task ConnectAsync_AlongsideATtySession_BothRevokeAndLeaveTheSharedKeyringEmpty()
    {
        var keyring = new SessionMcpKeyring();
        var mounts = new SessionMcpMounts();
        await using var gateway = new SessionMcpGatewayHost(new McpAuthKey(), keyring, NullLoggerFactory.Instance,
            Path.Combine(AppContext.BaseDirectory, $"gateway-{Guid.NewGuid():N}.json"), mounts: mounts);
        await gateway.StartAsync(CancellationToken.None);
        var provider = _ProviderFor(_DisableBuiltIns(), keyring, mounts: mounts, gateway: gateway);
        var ptyHostFactory = Substitute.For<IPtyHostFactory>();
        ptyHostFactory
            .Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<short>(), Arg.Any<short>())
            .Returns(Substitute.For<IConPtyProcess>());
        var ttyLauncher = new TtyLauncher(ptyHostFactory, Substitute.For<ISessionMemoryLimiter>(), keyring, NullLogger<TtyLauncher>.Instance);
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

        // Category A: a stopped gateway must refuse before minting a key or reaching presets/upstreams.
        await gateway.StopAsync(CancellationToken.None);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ConnectAsync(paneId: "dead-gateway"));
        Assert.Contains("gateway is unavailable", refused.Message);
        Assert.Equal(0, keyring.LivePaneCount);
    }

    private static McpToolProvider _ProviderFor(IEnumerable<McpServerConfig> registry, SessionMcpKeyring? keyring = null, IMcpOAuthAuthorizer? oauthAuthorizer = null, IMcpOAuthCoordinator? oauthCoordinator = null, ILogger<McpToolProvider>? logger = null, SessionMcpMounts? mounts = null, SessionMcpGatewayHost? gateway = null)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(registry.ToList());
        return new McpToolProvider(
            catalog,
            oauthAuthorizer ?? Substitute.For<IMcpOAuthAuthorizer>(),
            oauthCoordinator ?? Substitute.For<IMcpOAuthCoordinator>(),
            new McpAuthKey(),
            keyring ?? new SessionMcpKeyring(),
            logger ?? NullLogger<McpToolProvider>.Instance, mounts: mounts, gateway: gateway);
    }
}
