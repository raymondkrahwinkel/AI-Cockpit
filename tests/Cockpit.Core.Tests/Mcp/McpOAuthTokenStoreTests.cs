using Cockpit.Core.Mcp;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Core.Layout;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// Round-trip for the MCP OAuth tokens (AC-353) in <c>cockpit.json</c>, plus the naming that decides whether they are
/// encrypted at rest — <c>SecretFields</c> works on the field's name, so the names here are not cosmetic.
/// </summary>
public class McpOAuthTokenStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public McpOAuthTokenStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    private static McpOAuthToken _Token(string accessToken = "access") => new()
    {
        AccessToken = accessToken,
        RefreshToken = "refresh",
        Scheme = "Bearer",
        ExpiresAt = new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero),
        Scope = "mcp:read",
        ResourceUrl = "https://depot.example/mcp",
    };

    [Fact]
    public async Task GetAsync_WithNothingStored_IsNull()
    {
        Assert.Null(await new McpOAuthTokenStore(_configFilePath).GetAsync("depot"));
    }

    [Fact]
    public async Task SaveAsync_ThenGetAsync_RoundTripsTheToken()
    {
        var store = new McpOAuthTokenStore(_configFilePath);

        await store.SaveAsync("depot", _Token());
        var loaded = await store.GetAsync("depot");

        Assert.NotNull(loaded);
        Assert.Equal("access", loaded.AccessToken);
        Assert.Equal("refresh", loaded.RefreshToken);
        Assert.Equal("Bearer", loaded.Scheme);
        Assert.Equal("mcp:read", loaded.Scope);
        Assert.Equal("https://depot.example/mcp", loaded.ResourceUrl);
        Assert.Equal(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero), loaded.ExpiresAt);
    }

    [Fact]
    public async Task SaveAsync_Twice_ReplacesTheTokenRatherThanStackingThem()
    {
        var store = new McpOAuthTokenStore(_configFilePath);

        await store.SaveAsync("depot", _Token("first"));
        await store.SaveAsync("depot", _Token("second"));

        // A renewal happens on every refresh; a store that appended would grow a config file full of dead
        // credentials, and leave it ambiguous which one is current.
        Assert.Equal("second", (await store.GetAsync("depot"))?.AccessToken);
    }

    [Fact]
    public async Task GetAsync_MatchesTheServerNameCaseInsensitively()
    {
        var store = new McpOAuthTokenStore(_configFilePath);
        await store.SaveAsync("Depot", _Token());

        Assert.NotNull(await store.GetAsync("depot"));
    }

    [Fact]
    public async Task RemoveAsync_ForgetsTheToken_AndIsHarmlessWhenThereIsNone()
    {
        var store = new McpOAuthTokenStore(_configFilePath);
        await store.SaveAsync("depot", _Token());

        await store.RemoveAsync("depot");
        await store.RemoveAsync("depot");

        Assert.Null(await store.GetAsync("depot"));
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        await new McpOAuthTokenStore(_configFilePath).SaveAsync("depot", _Token());

        Assert.True((await layoutStore.LoadAsync()).SingleSessionLayout);
    }

    [Fact]
    public void TheTokenFieldsAreCoveredByTheSecretRule_AndTheSchemeIsNot()
    {
        // This is why the fields are called what they are: encryption and backup-scrubbing both key off the name, so
        // AccessToken/RefreshToken ride the existing "token" rule with no plumbing. The scheme holds the word
        // "Bearer" and is deliberately not called TokenType, which would have it needlessly encrypted.
        Assert.True(SecretFields.ByName.IsSecret(nameof(McpOAuthToken.AccessToken)));
        Assert.True(SecretFields.ByName.IsSecret(nameof(McpOAuthToken.RefreshToken)));
        Assert.False(SecretFields.ByName.IsSecret(nameof(McpOAuthToken.Scheme)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
