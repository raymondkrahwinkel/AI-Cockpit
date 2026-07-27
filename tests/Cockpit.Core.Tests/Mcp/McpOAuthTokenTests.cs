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
}
