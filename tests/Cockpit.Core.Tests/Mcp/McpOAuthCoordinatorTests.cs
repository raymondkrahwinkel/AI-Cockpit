using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpOAuthCoordinator"/> (AC-353): what a session may present to an OAuth-protected server, and what it
/// is told when there is nothing to present. The states have to be distinguishable — "needs no sign-in" and "needs
/// one nobody has done" lead to opposite things being said to the operator.
/// </summary>
public class McpOAuthCoordinatorTests
{
    // Port 1 is refused immediately rather than left hanging, so the renewal attempt fails deterministically and fast.
    private const string UnreachableUrl = "http://127.0.0.1:1/mcp";

    private static McpOAuthToken _TokenFor(string accessToken, string url, DateTimeOffset expiresAt, string? refreshToken = null) => new()
    {
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        ExpiresAt = expiresAt,
        ResourceUrl = url,
    };

    private static McpServerConfig _OAuthServer(string url = UnreachableUrl) => new()
    {
        Name = "depot",
        Transport = McpTransport.Http,
        Url = url,
        Auth = McpServerAuth.OAuth,
    };

    private static (McpOAuthCoordinator Coordinator, FakeMcpOAuthTokenStore Store) _Create()
    {
        var store = new FakeMcpOAuthTokenStore();
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);
        return (new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance), store);
    }

    [Fact]
    public async Task Acquire_ForAServerThatDoesNotUseOAuth_NeedsNothing_AndNeverReadsTheStore()
    {
        var (coordinator, store) = _Create();
        var apiKeyServer = new McpServerConfig
        {
            Name = "youtrack",
            Transport = McpTransport.Http,
            Url = "http://127.0.0.1:9000/mcp",
            Auth = McpServerAuth.ApiKey,
            ApiKey = "yt-pat-value",
        };

        var access = await coordinator.AcquireAsync(apiKeyServer, interactive: false);

        Assert.Equal(McpAuthState.NotRequired, access.State);
        Assert.Null(access.AccessToken);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Acquire_WithAStoredTokenThatIsStillGood_HandsItOver()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("depot-access-token", UnreachableUrl, DateTimeOffset.UtcNow.AddHours(1)));

        var access = await coordinator.AcquireAsync(_OAuthServer(), interactive: false);

        Assert.Equal(McpAuthState.Authorized, access.State);
        Assert.Equal("depot-access-token", access.AccessToken);
    }

    [Fact]
    public async Task Acquire_WhenTheNameNowPointsAtADifferentHost_RefusesTheStoredToken()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("depot-access-token", "https://depot.example/mcp", DateTimeOffset.UtcNow.AddHours(1), refreshToken: "refresh"));

        // A project can replace a registry server with its own entry under the same name and a different address
        // (ProjectMcpOverlay.ApplyTo), and a rename does the same. Handing the token over here would send one host's
        // credential to another — the refresh token is refused with it, since renewing would repeat the mistake.
        var access = await coordinator.AcquireAsync(_OAuthServer("https://attacker.example/mcp"), interactive: false);

        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
        Assert.Null(access.AccessToken);
    }

    [Fact]
    public async Task Acquire_WhenOnlyThePathMoved_StillUsesTheStoredToken()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("depot-access-token", "https://depot.example/mcp", DateTimeOffset.UtcNow.AddHours(1)));

        // Same party, different endpoint on it: the bearer still goes where it was issued to go, so re-authorizing
        // over a path edit would be a cost without a reason.
        var access = await coordinator.AcquireAsync(_OAuthServer("https://depot.example/mcp/v2"), interactive: false);

        Assert.Equal(McpAuthState.Authorized, access.State);
    }

    [Fact]
    public async Task Acquire_WhenNobodyHasSignedIn_ReportsAuthorizationRequired_WithoutAttemptingARenewal()
    {
        var (coordinator, store) = _Create();

        var access = await coordinator.AcquireAsync(_OAuthServer(), interactive: false);

        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
        Assert.Null(access.AccessToken);

        // One read, not two: with no refresh token there is nothing a handshake could achieve, and this runs on every
        // session start. A second read would mean the network was touched for a renewal that could not succeed.
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task Acquire_WhenTheStoredTokenIsStaleAndTheRenewalFails_ReportsAuthorizationRequired()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("expired-access-token", UnreachableUrl, DateTimeOffset.UtcNow.AddMinutes(-5), refreshToken: "refresh"));

        var access = await coordinator.AcquireAsync(_OAuthServer(), interactive: false);

        // The expired token is never handed out as a consolation prize — that would put a dead credential in a config
        // file and turn a nameable state into a 401 the session meets later with no way to act on it.
        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
        Assert.Null(access.AccessToken);

        // More than one read means a renewal was actually attempted (the opposite of the case above). Not pinned to
        // an exact number: the SDK consults the token cache itself during the handshake, so the count belongs to its
        // internals rather than to anything this test is about.
        Assert.True(store.Reads > 1, $"expected a renewal attempt, but the store was read {store.Reads} time(s)");
    }

    [Fact]
    public async Task GetState_ForAServerThatDoesNotUseOAuth_NeedsNoSignIn()
    {
        var (coordinator, _) = _Create();
        var apiKeyServer = new McpServerConfig
        {
            Name = "youtrack",
            Transport = McpTransport.Http,
            Url = "http://127.0.0.1:9000/mcp",
            Auth = McpServerAuth.ApiKey,
            ApiKey = "yt-pat-value",
        };

        Assert.Equal(McpAuthState.NotRequired, await coordinator.GetStateAsync(apiKeyServer));
    }

    [Fact]
    public async Task GetState_WithNothingStored_SaysASignInIsNeeded()
    {
        var (coordinator, _) = _Create();

        Assert.Equal(McpAuthState.AuthorizationRequired, await coordinator.GetStateAsync(_OAuthServer()));
    }

    [Fact]
    public async Task GetState_WithAValidToken_SaysSignedIn_WithoutTouchingTheNetwork()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("depot-access-token", UnreachableUrl, DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal(McpAuthState.Authorized, await coordinator.GetStateAsync(_OAuthServer()));

        // Exactly one read is the evidence that nothing else happened. A handshake consults the store again on its
        // way through — that is how the renewal path shows up in these tests — so a second read here would mean a
        // status had gone out to the network. A status is drawn for every server in a list; opening a dialog must
        // not become an event on somebody else's server, nor an opportunity for a browser window.
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task GetState_WithAnExpiredTokenThatCanStillBeRenewed_SaysSignedIn()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("expired", UnreachableUrl, DateTimeOffset.UtcNow.AddMinutes(-5), refreshToken: "refresh"));

        // Not the same question Acquire answers. Acquire keeps a margin because it writes a token into a config a
        // session then reads for an hour; a status answers "are you signed in", and this one renews itself on next
        // use without the operator doing anything — telling them to sign in again would be asking for nothing.
        Assert.Equal(McpAuthState.Authorized, await coordinator.GetStateAsync(_OAuthServer()));
    }

    [Fact]
    public async Task GetState_WithAnExpiredTokenAndNoWayToRenew_SaysASignInIsNeeded()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("expired", UnreachableUrl, DateTimeOffset.UtcNow.AddMinutes(-5)));

        Assert.Equal(McpAuthState.AuthorizationRequired, await coordinator.GetStateAsync(_OAuthServer()));
    }

    [Fact]
    public async Task GetState_ForATokenHeldUnderThisNameForADifferentHost_SaysASignInIsNeeded()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("token", "https://depot.example/mcp", DateTimeOffset.UtcNow.AddHours(1), refreshToken: "refresh"));

        // Same rule the credential path applies: a name is not an identity. Showing "signed in" for a host this
        // token was never issued to would be the status lying about exactly the case that matters.
        Assert.Equal(McpAuthState.AuthorizationRequired, await coordinator.GetStateAsync(_OAuthServer("https://elsewhere.example/mcp")));
    }

    [Fact]
    public async Task GetState_WithATokenAboutToExpireAndNoWayToRenew_SaysASignInIsNeeded()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("nearly-expired", UnreachableUrl, DateTimeOffset.UtcNow.AddSeconds(20)));

        // The status has to agree with what a session start will actually do. Acquire refuses this token — it keeps a
        // margin, because it writes into a config a session reads for an hour — so a badge saying "signed in" would
        // be wrong about precisely the case this feature exists to make visible.
        Assert.Equal(McpAuthState.AuthorizationRequired, await coordinator.GetStateAsync(_OAuthServer()));
    }

    [Fact]
    public async Task Acquire_AskedInteractively_DoesNotAnswerFromTheStoredToken()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("stored-token", UnreachableUrl, DateTimeOffset.UtcNow.AddHours(1), refreshToken: "refresh"));

        // "Sign in again" has to sign in again. Answering from the stored token would make the button do nothing —
        // and the reason someone presses it is usually that the stored token looks fine here while the server has
        // stopped honouring it. The server is unreachable in this test, so the flow cannot succeed and the answer is
        // that authorization is needed; what matters is that the stored token was not handed back instead.
        var access = await coordinator.AcquireAsync(_OAuthServer(), interactive: true);

        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
        Assert.Null(access.AccessToken);
        Assert.Null(await store.GetAsync("depot"));
    }

    [Fact]
    public async Task SignOut_ForgetsTheToken_SoTheNextUseAsksAgain()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("depot-access-token", UnreachableUrl, DateTimeOffset.UtcNow.AddHours(1)));

        await coordinator.SignOutAsync(_OAuthServer());

        Assert.Equal(McpAuthState.AuthorizationRequired, await coordinator.GetStateAsync(_OAuthServer()));
        Assert.Null(await store.GetAsync("depot"));
    }

    [Fact]
    public async Task SignOut_ForAServerWithNothingStored_IsHarmless()
    {
        var (coordinator, _) = _Create();

        await coordinator.SignOutAsync(_OAuthServer());
    }

    [Fact]
    public async Task Acquire_WhenTheTokenExpiresWithinTheMargin_DoesNotHandItOver()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", _TokenFor("nearly-expired", UnreachableUrl, DateTimeOffset.UtcNow.AddSeconds(20), refreshToken: "refresh"));

        var access = await coordinator.AcquireAsync(_OAuthServer(), interactive: false);

        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
    }
}
