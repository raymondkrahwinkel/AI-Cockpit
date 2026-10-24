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

    private static McpOAuthToken _TokenIssuedFor(string? url) =>
        new() { AccessToken = "access", ResourceUrl = url };

    [Theory]
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
    public void ToString_DoesNotPrintEitherTokenOrTheClientSecret()
    {
        var token = new McpOAuthToken
        {
            AccessToken = "the-access-token",
            RefreshToken = "the-refresh-token",
            ResourceUrl = "https://depot.example/mcp",
            ClientId = "the-client-id",
            ClientSecret = "the-client-secret",
        };

        // Iron Law #8: a record's generated ToString() prints every property, and this one ends up in log lines and
        // exception messages by accident rather than by decision. AC-505: a registration secret is a credential too;
        // the client id is not, and stays visible like the resource.
        var text = token.ToString();
        Assert.DoesNotContain("the-access-token", text);
        Assert.DoesNotContain("the-refresh-token", text);
        Assert.DoesNotContain("the-client-secret", text);
        Assert.Contains("https://depot.example/mcp", text);
        Assert.Contains("the-client-id", text);
    }
}
