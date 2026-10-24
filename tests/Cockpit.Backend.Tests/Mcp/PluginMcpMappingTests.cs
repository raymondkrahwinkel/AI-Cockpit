using Cockpit.Infrastructure.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Backend.Tests.Mcp;

/// <summary>
/// <see cref="PluginMcpMapping"/> (#60, AC-500): a plugin's <see cref="McpServerContribution"/> maps to the host's
/// <see cref="McpServerConfig"/>. Before this ticket, <see cref="PluginMcpMapping.ToAuth"/> only knew
/// <see cref="McpServerAuth.None"/>/<see cref="McpServerAuth.ApiKey"/> — a contribution declaring OAuth had no way
/// to say so and silently connected as <see cref="McpServerAuth.None"/> (McpToolProvider.cs:262 never reached).
/// </summary>
public class PluginMcpMappingTests
{
    // An OAuth contribution carries its authority and client id into the server config. AC-500 review finding: one
    // that (wrongly) also sets a bearer token must not leave that unused token in the registry beside the OAuth config.
    [Fact]
    public void ToServerConfig_OAuthContribution_CarriesAuthorityAndClientId_AndDropsAnUnusedBearerToken()
    {
        var contribution = new McpServerContribution("Depot: project-a", "https://depot.example/mcp", BearerToken: "stray-token")
        {
            OAuthAuthority = "https://depot.example/oauth",
            OAuthClientId = "cockpit",
        };

        var config = PluginMcpMapping.ToServerConfig(contribution);

        Assert.Equal(McpServerAuth.OAuth, config.Auth);
        Assert.Null(config.ApiKey);
        Assert.Equal("https://depot.example/oauth", config.OAuthAuthority);
        Assert.Equal("cockpit", config.OAuthClientId);
    }

    // The inverse: an ApiKey contribution must not carry a leftover/blank OAuth authority into the server config.
    [Fact]
    public void ToServerConfig_ApiKeyContribution_LeavesOAuthFieldsNull()
    {
        var contribution = new McpServerContribution("YouTrack: Prod", "https://x.youtrack.cloud/mcp", "token-123");

        var config = PluginMcpMapping.ToServerConfig(contribution);

        Assert.Equal(McpServerAuth.ApiKey, config.Auth);
        Assert.Equal("token-123", config.ApiKey);
        Assert.Null(config.OAuthAuthority);
        Assert.Null(config.OAuthClientId);
    }

    [Fact]
    public void ToServerConfig_ContributionWithAnId_KeysOnThatIdRatherThanOnTheName()
    {
        // AC-403: a plugin whose server name is built from something the operator renames (a Depot connection)
        // offers an id of its own, and that is what the token is filed under.
        var contribution = new McpServerContribution("Depot: work", "https://depot.example/mcp") { Id = " connection-id " };

        Assert.Equal("connection-id", PluginMcpMapping.ToServerConfig(contribution).IdentityKey);
    }

}
