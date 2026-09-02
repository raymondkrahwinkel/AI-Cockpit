using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// <see cref="McpToolProbe"/> (AC-503): one MCP tool call outside a running session. Covers the outcomes a plugin's
/// own reachability check depends on — an unknown server, a server that needs a sign-in, and the honesty rule that a
/// connection failure never reads as "not found" — plus the one surviving wegwerp-harnas case (AgentSpawnPlaybook §3)
/// run against the real class rather than a mock standing in for it.
/// </summary>
public class McpToolProbeTests
{
    private static IMcpServerStore _Store(params McpServerConfig[] servers)
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(servers.ToList());
        return store;
    }

    private static McpToolProbe _Probe(
        IMcpServerStore store, IMcpOAuthCoordinator? coordinator = null, IMcpOAuthAuthorizer? authorizer = null) =>
        new(store, coordinator ?? Substitute.For<IMcpOAuthCoordinator>(), authorizer ?? Substitute.For<IMcpOAuthAuthorizer>(),
            new McpAuthKey(), NullLogger<McpToolProbe>.Instance);

    [Fact]
    public async Task ProbeAsync_AnUnknownServerName_AnswersFailed_WithoutAttemptingAnything()
    {
        var probe = _Probe(_Store());

        var result = await probe.ProbeAsync("no-such-server", "outline", null);

        Assert.Equal(McpToolProbeOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ProbeAsync_AnOAuthServerNotSignedIn_AnswersNotSignedIn_WithoutConnecting()
    {
        var server = new McpServerConfig
        {
            Name = "depot", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", Auth = McpServerAuth.OAuth,
        };
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(server, Arg.Any<CancellationToken>()).Returns(McpAuthState.AuthorizationRequired);
        var authorizer = Substitute.For<IMcpOAuthAuthorizer>();
        var probe = _Probe(_Store(server), coordinator, authorizer);

        var result = await probe.ProbeAsync("depot", "outline", null);

        Assert.Equal(McpToolProbeOutcome.NotSignedIn, result.Outcome);
        // Not signed in means no connection attempt at all — this is the local, non-interactive read GetStateAsync
        // already promises, never a browser or a wasted round trip.
        authorizer.DidNotReceiveWithAnyArgs().CreateOptions(default!, default);
    }

    [Fact]
    public async Task ProbeAsync_AnUnreachableServer_AnswersFailed_NeverNotFound()
    {
        // AC-503 acceptance criterion 4: a connection that could not even be established must never be read as "this
        // value does not exist" — port 1 on loopback refuses immediately, standing in for "unreachable" without an
        // actual network dependency (the same trick CockpitMcpBearerTests already uses for a fast-failing address).
        var server = new McpServerConfig { Name = "unreachable", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" };
        var probe = _Probe(_Store(server));

        var result = await probe.ProbeAsync("unreachable", "outline", null);

        Assert.Equal(McpToolProbeOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ProbeAsync_AStoreThatThrows_AnswersFailed_RatherThanThrowing()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<McpServerConfig>>>(_ => throw new InvalidOperationException("disk error"));
        var probe = _Probe(store);

        var result = await probe.ProbeAsync("depot", "outline", null);

        Assert.Equal(McpToolProbeOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ProbeAsync_ACallersOwnCancellation_PropagatesRatherThanBeingSwallowedAsFailed()
    {
        var server = new McpServerConfig { Name = "unreachable", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" };
        var probe = _Probe(_Store(server));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.ProbeAsync("unreachable", "outline", null, cancellationToken: cts.Token));
    }

    // --- callerFallbackServers (AC-499) ---------------------------------------------------------------------------
    // CockpitHost hands this an additive candidate list scoped to the calling plugin's own contributions. These
    // tests exercise only the mechanism this class owns: the registry store is tried first, the fallback list only
    // when that finds nothing under the name, and a name absent from both never resolves.

    [Fact]
    public async Task ProbeAsync_NotInCallerFallback_TheOtherEntrysNameIsNeverMatched()
    {
        // The fallback list is matched by exact name, not treated as "anything in this list is fine" — the one
        // entry it carries is under a different name than what is asked for, so a match here would only be
        // possible if the lookup ignored the name (e.g. "first entry regardless of name").
        var probe = _Probe(_Store());
        var fallback = new List<McpServerConfig> { new() { Name = "own-server", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" } };

        var result = await probe.ProbeAsync("someone-elses-server", "outline", null, fallback);

        Assert.Equal(McpToolProbeOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ProbeAsync_CallerFallbackOAuthNeedingSignIn_AnswersNotSignedIn_WithoutConnecting()
    {
        var fallbackServer = new McpServerConfig
        {
            Name = "own-oauth-server", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", Auth = McpServerAuth.OAuth,
        };
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(fallbackServer, Arg.Any<CancellationToken>()).Returns(McpAuthState.AuthorizationRequired);
        var authorizer = Substitute.For<IMcpOAuthAuthorizer>();
        var probe = _Probe(_Store(), coordinator, authorizer);

        var result = await probe.ProbeAsync("own-oauth-server", "outline", null, [fallbackServer]);

        Assert.Equal(McpToolProbeOutcome.NotSignedIn, result.Outcome);
        authorizer.DidNotReceiveWithAnyArgs().CreateOptions(default!, default);
    }

    // --- Wegwerp-harnas (AgentSpawnPlaybook §3): hostile input against the real class -------------------------------

    // The one case here a realistic change can still break. Four siblings pushing hostile names and huge argument
    // dictionaries at 127.0.0.1:1 are gone: that port refuses on connect, so they measured the refusal, not the input.

    [Fact]
    public async Task Harness_ANonExistentTransport_NeverCrashesTheCaller()
    {
        // McpTransport has only Stdio/Http today, so this pins the _BuildTransport switch's default arm rather than
        // a value that could actually occur — cheap insurance if a third transport is ever added without updating it.
        var server = new McpServerConfig { Name = "weird", Transport = (McpTransport)99 };
        var probe = _Probe(_Store(server));

        var result = await probe.ProbeAsync("weird", "outline", null);

        Assert.Equal(McpToolProbeOutcome.Failed, result.Outcome);
    }
}
