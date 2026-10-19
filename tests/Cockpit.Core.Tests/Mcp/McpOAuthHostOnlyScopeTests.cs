using System.Net;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;

namespace Cockpit.Core.Tests.Mcp;

// AC-1586: a scope the cockpit keeps for itself (Depot's depot.inbox) never rides on a token a session is handed,
// whether it came from a sign-in just now, from a server that will not narrow, or from a store written before this.
public sealed class McpOAuthHostOnlyScopeTests
{
    private const string WideToken = "wide-access-token";
    private const string SessionToken = "session-access-token";

    private static readonly McpServerConfig Depot = new()
    {
        Id = "depot-1",
        Name = "Depot: Work",
        Transport = McpTransport.Http,
        Url = "http://127.0.0.1:9/mcp",
        Auth = McpServerAuth.OAuth,
        OAuthScopes = "depot depot.inbox offline_access",
        OAuthHostOnlyScopes = "depot.inbox",
    };

    [Theory]
    [InlineData("a sign-in on a server that narrows", "depot offline_access", SessionToken)]
    [InlineData("a sign-in on a server that ignores the scope", "depot depot.inbox offline_access", null)]
    [InlineData("a sign-in on a server that narrows without stating the scope", null, SessionToken)]
    [InlineData("a wide token stored before this change", "depot offline_access", null)]
    public async Task ASession_IsNeverHandedAHostOnlyScope(string row, string? narrowedScope, string? expected)
    {
        var tokenEndpoint = new _DepotTokenEndpoint(narrowedScope);
        var store = new FakeMcpOAuthTokenStore();
        var narrower = new McpOAuthScopeNarrower(NullLogger<McpOAuthScopeNarrower>.Instance, tokenEndpoint);
        var wide = new TokenContainer
        {
            AccessToken = WideToken,
            TokenType = "Bearer",
            RefreshToken = "refresh-1",
            ExpiresIn = 3600,
            Scope = "depot depot.inbox offline_access",
            ObtainedAt = DateTimeOffset.UtcNow,
            ClientId = "depot",
            AuthorizationServer = "http://depot.test",
        };
        if (row.StartsWith("a wide token", StringComparison.Ordinal))
        {
            await store.SaveAsync(Depot.IdentityKey, Depot.Name, new McpOAuthToken
            {
                AccessToken = WideToken,
                RefreshToken = "refresh-1",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
                Scope = wide.Scope,
                ResourceUrl = Depot.Url,
            });
        }
        else
        {
            var cache = new McpOAuthTokenCache(Depot.IdentityKey, Depot.Name, Depot.Url, store, NullLogger.Instance, hostOnlyScopes: (Depot, narrower));
            await cache.StoreTokensAsync(wide);
        }

        var coordinator = new McpOAuthCoordinator(store, new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store, narrower), NullLogger<McpOAuthCoordinator>.Instance);
        var forSession = await coordinator.AcquireForSessionAsync(Depot);

        Assert.Equal(expected, forSession.AccessToken);
        Assert.All(tokenEndpoint.Scopes, scope => Assert.Equal("depot offline_access", scope));
    }

    // Depot's token endpoint after DEP-415: a refresh with one scope parameter, answered with `stated` as its scope.
    private sealed class _DepotTokenEndpoint(string? stated) : HttpMessageHandler
    {
        public List<string?> Scopes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return _Json("""{"issuer":"http://depot.test","token_endpoint":"http://depot.test/connect/token"}""");
            }

            var form = System.Web.HttpUtility.ParseQueryString(request.Content is { } content ? await content.ReadAsStringAsync(cancellationToken) : string.Empty);
            Scopes.Add(form["scope"]);
            var scope = stated is null ? string.Empty : $""","scope":"{stated}" """;
            return _Json($$"""{"access_token":"{{(stated is null or "depot offline_access" ? SessionToken : WideToken)}}","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh-2"{{scope}}}""");
        }

        private static HttpResponseMessage _Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}
