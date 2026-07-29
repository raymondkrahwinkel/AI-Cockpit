using Cockpit.App.Plugins;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="PluginMcpMapping"/> (#60, AC-500): a plugin's <see cref="McpServerContribution"/> maps to the host's
/// <see cref="McpServerConfig"/>. Before this ticket, <see cref="PluginMcpMapping.ToAuth"/> only knew
/// <see cref="McpServerAuth.None"/>/<see cref="McpServerAuth.ApiKey"/> — a contribution declaring OAuth had no way
/// to say so and silently connected as <see cref="McpServerAuth.None"/> (McpToolProvider.cs:262 never reached).
/// </summary>
public class PluginMcpMappingTests
{
    [Fact]
    public void ToAuth_ContributionWithOAuthAuthority_MapsToOAuth()
    {
        var contribution = new McpServerContribution("Depot: project-a", "https://depot.example/mcp", OAuthAuthority: "https://depot.example/oauth");

        Assert.Equal(McpServerAuth.OAuth, PluginMcpMapping.ToAuth(contribution));
    }

    [Fact]
    public void ToAuth_ContributionWithOnlyABearerToken_MapsToApiKey()
    {
        var contribution = new McpServerContribution("YouTrack: Prod", "https://x.youtrack.cloud/mcp", "token-123");

        Assert.Equal(McpServerAuth.ApiKey, PluginMcpMapping.ToAuth(contribution));
    }

    [Fact]
    public void ToAuth_ContributionWithNeither_MapsToNone()
    {
        var contribution = new McpServerContribution("open-server", "https://open.example.com/mcp");

        Assert.Equal(McpServerAuth.None, PluginMcpMapping.ToAuth(contribution));
    }

    // A contribution that (wrongly) sets both must not silently degrade to a bearer-only auth that can never
    // satisfy the server's real OAuth requirement — OAuth wins.
    [Fact]
    public void ToAuth_ContributionWithBothOAuthAndBearer_OAuthWins()
    {
        var contribution = new McpServerContribution(
            "Depot: project-a", "https://depot.example/mcp", BearerToken: "stray-token", OAuthAuthority: "https://depot.example/oauth");

        Assert.Equal(McpServerAuth.OAuth, PluginMcpMapping.ToAuth(contribution));
    }

    [Fact]
    public void ToServerConfig_OAuthContribution_CarriesAuthorityAndClientIdIntoTheServerConfig()
    {
        var contribution = new McpServerContribution(
            "Depot: project-a", "https://depot.example/mcp", OAuthAuthority: "https://depot.example/oauth", OAuthClientId: "cockpit");

        var config = PluginMcpMapping.ToServerConfig(contribution);

        Assert.Equal(McpServerAuth.OAuth, config.Auth);
        Assert.Equal("https://depot.example/oauth", config.OAuthAuthority);
        Assert.Equal("cockpit", config.OAuthClientId);
        Assert.Null(config.ApiKey);
    }
}
