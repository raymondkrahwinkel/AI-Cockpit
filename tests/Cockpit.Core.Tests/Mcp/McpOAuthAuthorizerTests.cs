using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

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

    private static McpOAuthAuthorizer _Create(FakeMcpOAuthTokenStore store) =>
        new(NullLogger<McpOAuthAuthorizer>.Instance, store);

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

        Assert.NotNull(options.AuthorizationRedirectDelegate);
        var code = await options.AuthorizationRedirectDelegate(
            new Uri("https://depot.example/connect/authorize"),
            options.RedirectUri ?? new Uri("http://127.0.0.1:0/callback"),
            CancellationToken.None);

        // No authorization code means the SDK reports the flow as failed, which the coordinator reads as "this needs
        // the operator". The alternative — a browser opening because a session started — is the thing being prevented.
        Assert.Null(code);
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
}
