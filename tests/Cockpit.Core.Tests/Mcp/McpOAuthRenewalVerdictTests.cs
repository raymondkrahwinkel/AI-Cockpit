using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// What a silent renewal that did not work is reported as (AC-646) — driven against a real token endpoint, because
/// the distinction under test does not exist anywhere else.
/// <para>
/// The failure was measured: an <c>append</c> on Depot came back telling the operator the cockpit's sign-in "was
/// revoked or has run out", and the identical call a few minutes later succeeded with nothing signed in and nothing
/// restarted. There was no revoked grant. There was one renewal that did not work, reported as a permanent state —
/// and an agent that reads "authorize it again" stops working and waits for something nobody has to do.
/// </para>
/// <para>
/// The cause was that "expired" was reached by elimination: everything that was not recognisably a network failure
/// became <c>SignInExpired</c>, from a code path that had never once seen an authorization server say so. The SDK is
/// why — <c>ClientOAuthProvider.RefreshTokensAsync</c> returns null on any non-2xx without reading the body, so
/// <c>invalid_grant</c> cannot reach the caller as anything but the absence of a token. Hence a real server here
/// rather than a fake authorizer: the whole claim is about what the token endpoint actually answered.
/// </para>
/// </summary>
public class McpOAuthRenewalVerdictTests
{
    private static McpServerConfig _Server(string url) => new()
    {
        Id = "depot",
        Name = "depot",
        Transport = McpTransport.Http,
        Url = url,
        Auth = McpServerAuth.OAuth,
    };

    // A stale token with a refresh grant behind it — the state every silent renewal starts from. ClientId and
    // AuthorizationServer stand in for what a real sign-in persisted alongside the token; without them the SDK never
    // gets as far as presenting the refresh grant at all.
    private static async Task<FakeMcpOAuthTokenStore> _StoreWithAStaleTokenAsync(InProcessOAuthMcpServer server, string refreshToken)
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = "already-expired",
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ResourceUrl = server.Url,
            ClientId = InProcessOAuthMcpServer.ClientId,
            AuthorizationServer = server.BaseUrl,
        });

        return store;
    }

    private static McpOAuthCoordinator _Coordinator(FakeMcpOAuthTokenStore store, CapturingToastNotifier? toasts = null) =>
        new(
            store,
            new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store),
            NullLogger<McpOAuthCoordinator>.Instance,
            toasts);

    [Fact]
    public async Task Acquire_WhenTheAuthorizationServerRejectsTheGrant_ReportsAnExpiredSignIn()
    {
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        var store = await _StoreWithAStaleTokenAsync(server, refreshToken: "a-refresh-token-this-server-never-issued");

        var access = await _Coordinator(store).AcquireAsync(_Server(server.Url), interactive: false);

        // The other half of the fix, and the half a careless one loses. The token endpoint answered this refresh with
        // `invalid_grant` — an authorization server stating the grant is dead — so signing in again is exactly the
        // action, and softening it into "try again" would leave a genuinely revoked sign-in reported as weather.
        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
        Assert.Equal(McpOAuthAttentionReason.SignInExpired, access.Reason);
        Assert.Contains("press Sign in", McpOAuthSignInGuidance.For("depot", access.Reason), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acquire_WhenTheRenewalCouldNotBeConfirmed_DoesNotInterruptTheOperator()
    {
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        server.FailNextRefreshes(count: int.MaxValue);
        var store = await _StoreWithAStaleTokenAsync(server, InProcessOAuthMcpServer.RefreshToken);
        var toasts = new CapturingToastNotifier();

        await _Coordinator(store, toasts).AcquireAsync(_Server(server.Url), interactive: false);

        // The operator is interrupted when something of theirs is needed. Nothing here is: the call retries itself
        // and the next one works. A desktop notification reading "MCP server unavailable — sign in again" over a
        // storm that lasted a second is the same wrong claim as the tool error, just harder to ignore.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Empty(toasts.Shown);
    }
}
