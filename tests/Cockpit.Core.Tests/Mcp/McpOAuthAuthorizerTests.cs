using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpOAuthAuthorizer"/> (AC-353): the two things about the options that the rest of the feature rests on
/// — that the token is stored somewhere the cockpit can reach it afterwards, and that a caller who is not the
/// operator cannot make a browser window appear.
/// </summary>
public class McpOAuthAuthorizerTests
{
    private static readonly McpServerConfig Server = new()
    {
        Name = "depot",
        Transport = McpTransport.Http,
        Url = "https://depot.example/mcp",
        Auth = McpServerAuth.OAuth,
    };

    private static readonly Uri AuthorizationUri = new("https://depot.example/connect/authorize");

    /// <summary>A scheme the real hand-off refuses to give the shell, so nothing can take it.</summary>
    private static readonly Uri NonBrowsableAuthorizationUri = new("cockpit-test:authorize");

    private static McpOAuthAuthorizer _Create(FakeMcpOAuthTokenStore store) =>
        new(NullLogger<McpOAuthAuthorizer>.Instance, store);

    /// <summary>
    /// An authorizer whose hand-off to the desktop is stubbed, so the loopback half of the flow can be driven
    /// without a real browser window appearing on the machine running the suite.
    /// </summary>
    private static McpOAuthAuthorizer _CreateWithBrowser(bool tookTheUrl) =>
        new(NullLogger<McpOAuthAuthorizer>.Instance, new FakeMcpOAuthTokenStore()) { BrowserOpener = _ => tookTheUrl };

    // The listener is bound before the delegate's first await, so the callback can be sent straight away.
    private static async Task _DriveTheRedirectAsync(Uri redirectUri, string query)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri($"{redirectUri}?{query}"));
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task CreateOptions_PointsTheTokenCacheAtTheCockpitsOwnStorage()
    {
        var store = new FakeMcpOAuthTokenStore();

        var options = _Create(store).CreateOptions(Server);

        // Left unset, the SDK caches with the transport and the token dies with the connection — no injection, and a
        // fresh browser login after every restart. Proven by behaviour rather than by type: what the SDK writes to
        // this cache has to land in the cockpit's store.
        Assert.NotNull(options.TokenCache);
        await options.TokenCache.StoreTokensAsync(new ModelContextProtocol.Authentication.TokenContainer
        {
            AccessToken = "written-through",
            TokenType = "Bearer",
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        Assert.Equal("written-through", (await store.GetAsync("depot"))?.AccessToken);
    }

    [Fact]
    public async Task CreateOptions_NonInteractive_DeclinesTheSignIn_RatherThanOpeningABrowser()
    {
        var options = _Create(new FakeMcpOAuthTokenStore()).CreateOptions(Server, interactive: false);

        Assert.NotNull(options.AuthorizationCallbackHandler);
        var result = await options.AuthorizationCallbackHandler(
            new AuthorizationCallbackContext
            {
                AuthorizationUri = new Uri("https://depot.example/connect/authorize"),
                RedirectUri = options.RedirectUri ?? new Uri("http://127.0.0.1:0/callback"),
            },
            CancellationToken.None);

        // No authorization result means the SDK reports the flow as failed, which the coordinator reads as "this
        // needs the operator". The alternative — a browser opening because a session started — is the thing being
        // prevented.
        Assert.Null(result);
    }

    [Fact]
    public async Task CreateOptions_Interactive_RecordsThatAnAuthorizationCameBack()
    {
        var stageRecorder = new McpSignInStageRecorder();
        var options = _CreateWithBrowser(tookTheUrl: true).CreateOptions(Server, interactive: true, stageRecorder);
        Assert.NotNull(options.AuthorizationCallbackHandler);
        Assert.NotNull(options.RedirectUri);

        // The authorization step waits on its listener for as long as it takes; a broken assumption here would hang
        // the suite rather than fail it, so the wait gets an outer bound.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var authorization = options.AuthorizationCallbackHandler(
            new AuthorizationCallbackContext { AuthorizationUri = AuthorizationUri, RedirectUri = options.RedirectUri },
            timeout.Token);

        // Read before the redirect is driven, which is the only moment the hand-off stage stands alone: the URL has
        // gone to the desktop and nothing has arrived back yet. That is what an operator who closes the tab leaves
        // behind, and it is the stage whose sentence says nothing came back.
        Assert.Equal(McpSignInStage.BrowserRequested, stageRecorder.Reached);

        await _DriveTheRedirectAsync(options.RedirectUri, "code=the-code");

        Assert.Equal("the-code", (await authorization)?.Code);
        Assert.Equal(McpSignInStage.AuthorizationReturned, stageRecorder.Reached);
    }

    [Fact]
    public async Task CreateOptions_Interactive_WhenTheRedirectBringsARefusal_StillCountsAsComingBack()
    {
        var stageRecorder = new McpSignInStageRecorder();
        var options = _CreateWithBrowser(tookTheUrl: true).CreateOptions(Server, interactive: true, stageRecorder);
        Assert.NotNull(options.AuthorizationCallbackHandler);
        Assert.NotNull(options.RedirectUri);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var authorization = options.AuthorizationCallbackHandler(
            new AuthorizationCallbackContext { AuthorizationUri = AuthorizationUri, RedirectUri = options.RedirectUri },
            timeout.Token);
        await _DriveTheRedirectAsync(options.RedirectUri, "error=access_denied");

        // The operator pressed Deny and watched this listener answer their own browser tab. Holding the stage back
        // because no code came with it would tell them nothing came back — contradicting what they just read on
        // screen. The stage marks arrival; whether anything usable arrived is the next sentence's business.
        Assert.Null(await authorization);
        Assert.Equal(McpSignInStage.AuthorizationReturned, stageRecorder.Reached);
    }

    [Fact]
    public async Task CreateOptions_Interactive_WhenNothingTookTheUrl_GivesUpAndClaimsNoBrowser()
    {
        var stageRecorder = new McpSignInStageRecorder();
        var options = _Create(new FakeMcpOAuthTokenStore()).CreateOptions(Server, interactive: true, stageRecorder);
        Assert.NotNull(options.AuthorizationCallbackHandler);
        Assert.NotNull(options.RedirectUri);

        // No redirect is driven, and none can be: nothing took the URL. The step used to block on its listener for a
        // callback that could never arrive, leaving the operator on a spinner with no message at all — worse than
        // the wrong message AC-457 set out to remove. Pinned as "already finished" rather than as a short wait,
        // because a wait that comes back on a timeout would pass this test by taking half a minute over it.
        var authorization = options.AuthorizationCallbackHandler(
            new AuthorizationCallbackContext { AuthorizationUri = NonBrowsableAuthorizationUri, RedirectUri = options.RedirectUri },
            CancellationToken.None);

        Assert.True(authorization.IsCompleted, "the authorization step waited for a redirect that can never arrive");

        // And the stage is recorded where it is reached, never on the way in: a hand-off that did not happen must
        // not leave a stage behind that the dialog would turn into a browser the operator should go and look at.
        Assert.Null(await authorization);
        Assert.Equal(McpSignInStage.NoBrowserLaunched, stageRecorder.Reached);
    }

    [Fact]
    public void CreateOptions_UsesAnEphemeralLoopbackRedirect()
    {
        var options = _Create(new FakeMcpOAuthTokenStore()).CreateOptions(Server);

        // RFC 8252 §7.3 requires an authorization server to accept any port on a loopback redirect, so a free port
        // per attempt is correct and a fixed one would be a workaround for a server that breaks the RFC (AC-353,
        // and why DEP-103 fixed this on Depot's side instead).
        Assert.NotNull(options.RedirectUri);
        Assert.Equal("127.0.0.1", options.RedirectUri.Host);
        Assert.Equal("/callback", options.RedirectUri.AbsolutePath);
    }

    [Fact]
    public void CreateOptions_WithConfiguredOAuthScopes_SetsAScopeSelectorThatReplacesWhateverWasDerived()
    {
        var server = Server with { OAuthScopes = "depot custom-scope" };

        var options = _Create(new FakeMcpOAuthTokenStore()).CreateOptions(server);

        // ClientOAuthOptions.Scopes is only ever a fallback the SDK falls through to when a server gave it nothing
        // to derive from — it cannot override a server that advertises its own scopes_supported. ScopeSelector runs
        // after that derivation and replaces it outright, which is what a per-server override (AC-505 criterion 3)
        // actually needs. Fed an unrelated candidate list (standing in for whatever the SDK derived), it must still
        // come back with exactly the configured scopes.
        Assert.NotNull(options.ScopeSelector);
        var selected = options.ScopeSelector(["openid", "offline_access", "depot"]);
        Assert.Equal(["depot", "custom-scope"], selected);
    }

    [Fact]
    public void CreateOptions_WithoutConfiguredOAuthScopes_LeavesTheDerivationUntouched()
    {
        var options = _Create(new FakeMcpOAuthTokenStore()).CreateOptions(Server);

        // No override configured — the SDK's own WWW-Authenticate → PRM → offline_access derivation must run
        // unchanged, which means nothing here should intercept it.
        Assert.Null(options.ScopeSelector);
    }
}
