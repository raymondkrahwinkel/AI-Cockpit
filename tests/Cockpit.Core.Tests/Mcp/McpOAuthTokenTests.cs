using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpOAuthToken.IsUsableAt"/> (AC-353): the margin is the point. A config file is written once at session
/// start and read for as long as the session runs, so "still valid right now" is the wrong question — the question is
/// whether it will still be valid long enough to be worth writing down.
/// </summary>
public class McpOAuthTokenTests
{
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
    public void ToString_DoesNotPrintEitherToken()
    {
        var token = new McpOAuthToken
        {
            AccessToken = "the-access-token",
            RefreshToken = "the-refresh-token",
            ClientId = "the-client-id",
            ClientSecret = "the-client-secret",
            ResourceUrl = "https://depot.example/mcp",
        };

        // Iron Law #8: a record's generated ToString() prints every property, and this one ends up in log lines and
        // exception messages by accident rather than by decision. Same guard PluginMcpServer carries.
        var text = token.ToString();
        Assert.DoesNotContain("the-access-token", text);
        Assert.DoesNotContain("the-refresh-token", text);
        Assert.DoesNotContain("the-client-secret", text);
        Assert.Contains("the-client-id", text);
        Assert.Contains("https://depot.example/mcp", text);
    }
}
