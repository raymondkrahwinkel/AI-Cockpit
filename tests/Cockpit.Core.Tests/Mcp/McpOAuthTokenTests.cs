using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpOAuthToken.IsUsableAt"/> (AC-353): the margin is the point. A config file is written once at session
/// start and read for as long as the session runs, so "still valid right now" is the wrong question — the question is
/// whether it will still be valid long enough to be worth writing down.
/// </summary>
public class McpOAuthTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static McpOAuthToken _TokenExpiringAt(DateTimeOffset? expiresAt) =>
        new() { AccessToken = "access", ExpiresAt = expiresAt };

    [Fact]
    public void IsUsableAt_WithPlentyOfLifeLeft_IsUsable()
    {
        Assert.True(_TokenExpiringAt(Now.AddHours(1)).IsUsableAt(Now, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void IsUsableAt_WhenItExpiresInsideTheMargin_IsNotUsable()
    {
        // Unexpired, and still refused: handing this over writes a credential into a config that dies a minute later.
        Assert.False(_TokenExpiringAt(Now.AddSeconds(30)).IsUsableAt(Now, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void IsUsableAt_WhenAlreadyExpired_IsNotUsable()
    {
        Assert.False(_TokenExpiringAt(Now.AddMinutes(-1)).IsUsableAt(Now, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void IsUsableAt_WhenTheServerNamedNoExpiry_IsTakenAtFaceValue()
    {
        // Guessing a lifetime would either throw away a working credential or claim a dead one; neither is knowable
        // from here, so a token without a stated expiry is trusted until the server says otherwise.
        Assert.True(_TokenExpiringAt(null).IsUsableAt(Now, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void IsUsableAt_WithoutAnAccessToken_IsNotUsable()
    {
        var empty = new McpOAuthToken { AccessToken = "  ", ExpiresAt = Now.AddHours(1) };

        Assert.False(empty.IsUsableAt(Now, TimeSpan.FromMinutes(2)));
    }

    private static McpOAuthToken _TokenIssuedFor(string? url) =>
        new() { AccessToken = "access", ResourceUrl = url };

    [Theory]
    [InlineData("https://depot.example/mcp", true)]
    [InlineData("https://depot.example/mcp/v2", true)]
    [InlineData("https://DEPOT.example/mcp", true)]
    [InlineData("https://depot.example:8443/mcp", false)]
    [InlineData("http://depot.example/mcp", false)]
    [InlineData("https://somewhere-else.example/mcp", false)]
    public void IsForResource_MatchesOnTheOrigin_NotOnTheWholeAddress(string url, bool expected)
    {
        // Scheme, host and port decide who receives the bearer. A path that moved is the same party; a host, a port
        // or a downgrade to plain http is not, and a token must not follow a name across that line.
        Assert.Equal(expected, _TokenIssuedFor("https://depot.example/mcp").IsForResource(url));
    }

    [Fact]
    public void IsForResource_WithNoRecordedOrigin_IsNeverUsed()
    {
        Assert.False(_TokenIssuedFor(null).IsForResource("https://depot.example/mcp"));
    }

    [Fact]
    public void IsForResource_AgainstNoAddressAtAll_IsFalse()
    {
        Assert.False(_TokenIssuedFor("https://depot.example/mcp").IsForResource(null));
    }

    [Fact]
    public void ToString_DoesNotPrintEitherToken()
    {
        var token = new McpOAuthToken
        {
            AccessToken = "the-access-token",
            RefreshToken = "the-refresh-token",
            ResourceUrl = "https://depot.example/mcp",
        };

        // Iron Law #8: a record's generated ToString() prints every property, and this one ends up in log lines and
        // exception messages by accident rather than by decision. Same guard PluginMcpServer carries.
        var text = token.ToString();
        Assert.DoesNotContain("the-access-token", text);
        Assert.DoesNotContain("the-refresh-token", text);
        Assert.Contains("https://depot.example/mcp", text);
    }
}
