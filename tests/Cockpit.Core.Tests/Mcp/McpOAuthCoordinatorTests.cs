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
        Id = "depot",
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
    public async Task Acquire_WhenTheNameNowPointsAtADifferentHost_RefusesTheStoredToken()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", "depot", _TokenFor("depot-access-token", "https://depot.example/mcp", DateTimeOffset.UtcNow.AddHours(1), refreshToken: "refresh"));

        // A project can replace a registry server with its own entry under the same name and a different address
        // (ProjectMcpOverlay.ApplyTo), and a rename does the same. Handing the token over here would send one host's
        // credential to another — the refresh token is refused with it, since renewing would repeat the mistake.
        var access = await coordinator.AcquireAsync(_OAuthServer("https://attacker.example/mcp"), interactive: false);

        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
        Assert.Null(access.AccessToken);
    }

    [Fact]
    public async Task GetState_ForATokenHeldUnderThisNameForADifferentHost_SaysASignInIsNeeded()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", "depot", _TokenFor("token", "https://depot.example/mcp", DateTimeOffset.UtcNow.AddHours(1), refreshToken: "refresh"));

        // Same rule the credential path applies: a name is not an identity. Showing "signed in" for a host this
        // token was never issued to would be the status lying about exactly the case that matters.
        Assert.Equal(McpAuthState.AuthorizationRequired, await coordinator.GetStateAsync(_OAuthServer("https://elsewhere.example/mcp")));
    }

    [Fact]
    public async Task Acquire_AskedInteractively_PutsTheOldTokenBack_WhenTheFlowProducesNothing()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", "depot", _TokenFor("stored-token", UnreachableUrl, DateTimeOffset.UtcNow.AddHours(1), refreshToken: "refresh"));

        await coordinator.AcquireAsync(_OAuthServer(), interactive: true);

        // Clearing first is mechanically necessary — the SDK answers from the cache, so leaving the token in place
        // means the flow never runs. But closing the browser window should not cost the access you already had: one
        // click on "Sign in again" would otherwise destroy a working credential with no way back.
        var stored = await store.GetAsync("depot");
        Assert.Equal("stored-token", stored?.AccessToken);
        Assert.Equal("refresh", stored?.RefreshToken);
    }

    [Fact]
    public async Task SignOut_ForgetsTheToken_SoTheNextUseAsksAgain()
    {
        var (coordinator, store) = _Create();
        await store.SaveAsync("depot", "depot", _TokenFor("depot-access-token", UnreachableUrl, DateTimeOffset.UtcNow.AddHours(1)));

        await coordinator.SignOutAsync(_OAuthServer());

        Assert.Equal(McpAuthState.AuthorizationRequired, await coordinator.GetStateAsync(_OAuthServer()));
        Assert.Null(await store.GetAsync("depot"));
    }

}
