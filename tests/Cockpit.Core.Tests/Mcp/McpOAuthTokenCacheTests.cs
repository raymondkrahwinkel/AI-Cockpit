using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using ModelContextProtocol.Authentication;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpOAuthTokenCache"/> (AC-353): the bridge between the MCP client's token cache and the cockpit's own
/// storage. The conversion is not symmetric — the SDK counts a lifetime in seconds from when it obtained the token,
/// while storage has to survive a restart and therefore records an absolute instant.
/// </summary>
public class McpOAuthTokenCacheTests
{
    private static (McpOAuthTokenCache Cache, FakeMcpOAuthTokenStore Store) _Create()
    {
        var store = new FakeMcpOAuthTokenStore();
        return (new McpOAuthTokenCache("depot", store), store);
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
    }

    [Fact]
    public async Task GetTokens_RebasesTheRemainingLifetimeOnNow()
    {
        var (cache, store) = _Create();
        await store.SaveAsync("depot", new McpOAuthToken
        {
            AccessToken = "access",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(600),
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
        await store.SaveAsync("depot", new McpOAuthToken
        {
            AccessToken = "access",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
        });

        var container = await cache.GetTokensAsync();

        Assert.NotNull(container);
        Assert.Equal(0, container.ExpiresIn);
    }

    [Fact]
    public async Task GetTokens_WithNothingStored_IsNull()
    {
        var (cache, _) = _Create();

        Assert.Null(await cache.GetTokensAsync());
    }

    [Fact]
    public async Task StoreTokens_WithoutAnAccessToken_StoresNothing()
    {
        var (cache, store) = _Create();

        await cache.StoreTokensAsync(new TokenContainer { AccessToken = string.Empty, TokenType = "Bearer", ObtainedAt = DateTimeOffset.UtcNow });

        Assert.Null(await store.GetAsync("depot"));
    }
}
