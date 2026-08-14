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
    public async Task StoreTokens_WithoutARefreshToken_KeepsTheOneAlreadyHeld()
    {
        var (cache, store) = _Create();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = "old-access",
            RefreshToken = "the-refresh-token",
            ResourceUrl = ResourceUrl,
        });

        // RFC 6749 §6 lets a refresh response omit the refresh token, meaning "keep the one you have". Discarding it
        // would ask the operator to sign in again at the next expiry, against any server that does not rotate.
        await cache.StoreTokensAsync(new TokenContainer
        {
            AccessToken = "new-access",
            TokenType = "Bearer",
            ExpiresIn = 3600,
            ObtainedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var stored = await store.GetAsync("depot");
        Assert.Equal("new-access", stored?.AccessToken);
        Assert.Equal("the-refresh-token", stored?.RefreshToken);
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
    public async Task GetTokens_RebasesTheRemainingLifetimeOnNow()
    {
        var (cache, store) = _Create();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = "access",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(600),
            ResourceUrl = ResourceUrl,
        });

        var container = await cache.GetTokensAsync();

        // Handing back the stored ExpiresIn with a fresh ObtainedAt would present a nearly dead token as brand new,
        // so the remaining life is recomputed against the instant it is asked for: ten minutes left, not an hour.
        Assert.NotNull(container);
        Assert.InRange(container.ExpiresIn ?? 0, 590, 600);
    }

    [Fact]
    public async Task GetTokens_ForAnExpiredToken_ReportsNoLifeLeft_RatherThanANegativeOne()
    {
        var (cache, store) = _Create();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = "access",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            ResourceUrl = ResourceUrl,
        });

        var container = await cache.GetTokensAsync();

        Assert.NotNull(container);
        Assert.Equal(0, container.ExpiresIn);
    }

    [Fact]
    public async Task GetTokens_ForATokenInsideTheCallersMargin_ReportsItSpent_SoTheRenewalActuallyHappens()
    {
        var (cache, store) = _Create(renewalMargin: TimeSpan.FromMinutes(2));
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(90),
            ResourceUrl = ResourceUrl,
        });

        var container = await cache.GetTokensAsync();

        // The whole of AC-771. `TokenContainer.IsExpired` is the SDK's only test for whether to spend the refresh
        // grant, and it has no margin of its own — so a token with ninety seconds left is one the SDK would hand
        // straight back while the coordinator, keeping two minutes, had already decided it needed renewing. The
        // connect then changed nothing, the coordinator read the unchanged store as a failed renewal, and an
        // ordinary call came back to the agent as an authentication error until the token was properly dead.
        Assert.NotNull(container);
        Assert.True(_SdkWouldRenew(container));
    }

    [Fact]
    public async Task GetTokens_ForATokenOutsideTheCallersMargin_LeavesItAlone()
    {
        var (cache, store) = _Create(renewalMargin: TimeSpan.FromMinutes(2));
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            ResourceUrl = ResourceUrl,
        });

        var container = await cache.GetTokensAsync();

        // The mirror of the fix, and the one it would be easy to break: a margin that reports everything as spent
        // would rotate the refresh grant on every single connect, which is the outage `_renewals` exists to avoid.
        Assert.NotNull(container);
        Assert.False(_SdkWouldRenew(container));
    }

    // `TokenContainer.IsExpired` restated, because the SDK keeps it internal. It is the single condition on which a
    // refresh grant is spent, and it carries no margin of its own — which is exactly why the margin has to be
    // subtracted on the way out of the store rather than kept on this side of the line.
    private static bool _SdkWouldRenew(TokenContainer token) =>
        token.ExpiresIn is { } seconds && DateTimeOffset.UtcNow >= token.ObtainedAt.AddSeconds(seconds);

    [Fact]
    public async Task GetTokens_WithNothingStored_IsNull()
    {
        var (cache, _) = _Create();

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

    [Fact]
    public async Task StoreTokens_WithoutAnAccessToken_StoresNothing()
    {
        var (cache, store) = _Create();

        await cache.StoreTokensAsync(new TokenContainer { AccessToken = string.Empty, TokenType = "Bearer", ObtainedAt = DateTimeOffset.UtcNow });

        Assert.Null(await store.GetAsync("depot"));
    }
}
