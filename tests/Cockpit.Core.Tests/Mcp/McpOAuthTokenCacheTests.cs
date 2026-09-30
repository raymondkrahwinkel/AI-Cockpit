using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpOAuthTokenCache"/> (AC-353): the bridge between the MCP client's token cache and the cockpit's own
/// storage. The conversion is not symmetric — the SDK counts a lifetime in seconds from when it obtained the token,
/// while storage has to survive a restart and therefore records an absolute instant.
/// </summary>
public class McpOAuthTokenCacheTests
{
    private const string ResourceUrl = "https://depot.example/mcp";

    private static (McpOAuthTokenCache Cache, FakeMcpOAuthTokenStore Store) _Create(
        string resourceUrl = ResourceUrl,
        TimeSpan renewalMargin = default)
    {
        var store = new FakeMcpOAuthTokenStore();
        return (new McpOAuthTokenCache("depot", "depot", resourceUrl, store, NullLogger.Instance, renewalMargin), store);
    }

    [Fact]
    public async Task StoreTokens_RecordsTheAbsoluteExpiry_NotTheRelativeOne()
    {
        var (cache, store) = _Create();
        var obtainedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        await cache.StoreTokensAsync(new TokenContainer
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ObtainedAt = obtainedAt,
        });

        var stored = await store.GetAsync("depot");
        Assert.NotNull(stored);
        Assert.Equal("access", stored.AccessToken);
        Assert.Equal("refresh", stored.RefreshToken);
        Assert.Equal(obtainedAt.AddSeconds(3600), stored.ExpiresAt);

        // Recorded with the address it was obtained for, which is what later stops it being handed to a server that
        // has taken over the name.
        Assert.Equal(ResourceUrl, stored.ResourceUrl);
    }

    [Fact]
    public async Task StoreTokens_DoesNotInheritARefreshTokenHeldForADifferentHost()
    {
        var (cache, store) = _Create("https://depot.example/mcp");
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = "old-access",
            RefreshToken = "somebody-elses-refresh-token",
            ResourceUrl = "https://somewhere-else.example/mcp",
        });

        await cache.StoreTokensAsync(new TokenContainer
        {
            AccessToken = "new-access",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        // Keeping what you have must not mean keeping what belonged to another host: the record is found by name, and
        // if the name has changed hands, inheriting its grant would launder one host's credential into another's.
        var stored = await store.GetAsync("depot");
        Assert.Equal("new-access", stored?.AccessToken);
        Assert.Null(stored?.RefreshToken);
    }

    [Fact]
    public async Task GetTokens_ForATokenIssuedToADifferentHost_IsNull()
    {
        var (cache, store) = _Create("https://depot.example/mcp");
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = "access",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            ResourceUrl = "https://somewhere-else.example/mcp",
        });

        // The in-process route reads the cache to decide what header to send, so the same rule has to hold here as on
        // the spawn path: a name is not an identity, and the credential must not follow the name to another host.
        Assert.Null(await cache.GetTokensAsync());
    }

    [Fact]
    public async Task StoreThenGetTokens_RoundTripsTheClientIdentity()
    {
        // AC-505: without these, a refresh token stored here is unusable beyond the connection that obtained it —
        // the SDK only attempts a refresh grant once it has a client identity to present, and it restores that
        // identity from exactly these fields (ClientOAuthProvider.RestoreCachedClientCredentials).
        var (cache, store) = _Create();

        await cache.StoreTokensAsync(new TokenContainer
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ObtainedAt = DateTimeOffset.UtcNow,
            ClientId = "dcr-client",
            ClientSecret = "dcr-secret",
            TokenEndpointAuthMethod = "client_secret_post",
            AuthorizationServer = "https://depot.example",
        });

        var container = await cache.GetTokensAsync();

        Assert.NotNull(container);
        Assert.Equal("dcr-client", container.ClientId);
        Assert.Equal("dcr-secret", container.ClientSecret);
        Assert.Equal("client_secret_post", container.TokenEndpointAuthMethod);
        Assert.Equal("https://depot.example", container.AuthorizationServer);
    }

}
